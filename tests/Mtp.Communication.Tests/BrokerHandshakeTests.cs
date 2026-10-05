using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Mtp.Contracts;
using Mtp.Transport;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class BrokerHandshakeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("unregistered", "InvalidTicket")]
    [InlineData("wrong-application", "InvalidTicket")]
    [InlineData("wrong-start", "InvalidTicket")]
    [InlineData("wrong-ticket", "InvalidTicket")]
    [InlineData("expired", "TicketExpired")]
    [InlineData("unsupported-version", "UnsupportedVersion")]
    [InlineData("mixed-flyout", "InvalidHandshake")]
    public async Task Independent_broker_rejects_invalid_launch_identity(string scenario, string expectedCode)
    {
        await using var broker = await BrokerFixture.StartAsync(output, expired: scenario == "expired");
        var hello = broker.Hello() with { RequestId = scenario };
        hello = scenario switch
        {
            "unregistered" => hello with { ApplicationId = "not-registered" },
            "wrong-application" => hello with { ApplicationId = "registered-other" },
            "wrong-start" => hello with { StartRequestId = "unregistered-start" },
            "wrong-ticket" => hello with { Ticket = "not-the-ticket" },
            "unsupported-version" => hello with { Version = ProtocolLimits.Version + 1 },
            "mixed-flyout" => hello with { Flyout = new("injected", 1, "main", "event", FlyoutKind.EventGroup, 0) },
            _ => hello,
        };
        await using var service = await broker.ConnectServiceAsync();
        var response = await broker.ExchangeAsync(service, hello);
        Assert.Equal(MessageKind.Result, response.Kind);
        Assert.Equal(scenario, response.RequestId);
        Assert.NotNull(response.Result);
        Assert.False(response.Result.Accepted);
        Assert.Equal(expectedCode, response.Result.Code);
        output.WriteLine($"brokerPid={broker.ProcessId}; scenario={scenario}; rejection={response.Result.Code}");
        Assert.False(broker.HasExited);
        await broker.CloseHostAndWaitForExitAsync();
    }

    [Fact]
    public async Task Consumed_ticket_is_rejected_without_displacing_the_current_session()
    {
        await using var broker = await BrokerFixture.StartAsync(output);
        await using var original = await broker.ConnectServiceAsync();
        var welcome = await broker.ExchangeAsync(original, broker.Hello());
        Assert.Equal(MessageKind.Welcome, welcome.Kind);
        Assert.False(string.IsNullOrEmpty(welcome.SessionId));
        await using var replay = await broker.ConnectServiceAsync();
        var rejection = await broker.ExchangeAsync(replay, broker.Hello());
        Assert.Equal("InvalidTicket", rejection.Result?.Code);
        Assert.False(rejection.Result!.Accepted);

        // A valid session still gets its own structured protocol response after the replay.
        var current = await broker.ExchangeAsync(original, new ProtocolMessage
        {
            Kind = MessageKind.Hello,
            ApplicationId = welcome.ApplicationId,
            SessionId = welcome.SessionId,
            RequestId = "current-still-connected",
        });
        Assert.Equal("InvalidEnvelope", current.Result?.Code);
        output.WriteLine($"brokerPid={broker.ProcessId}; replay=InvalidTicket; originalSessionStillConnected=true");
        await broker.CloseHostAndWaitForExitAsync();
    }

    [Fact]
    public async Task Half_frame_handshake_expires_and_a_healthy_service_can_still_connect()
    {
        await using var broker = await BrokerFixture.StartAsync(output);
        await using var stalled = await broker.ConnectServiceAsync();
        var started = Stopwatch.StartNew();
        await stalled.WriteAsync(new byte[] { 28, 0 }, broker.Token);
        var closed = new byte[1];
        var disconnected = false;
        try { disconnected = await stalled.ReadAsync(closed, broker.Token) == 0; }
        catch (IOException) { disconnected = true; }
        Assert.True(disconnected, "The incomplete handshake must be closed without a response frame.");
        Assert.InRange(started.Elapsed.TotalSeconds, 4, 8);

        await using var healthy = await broker.ConnectServiceAsync();
        var welcome = await broker.ExchangeAsync(healthy, broker.Hello());
        Assert.Equal(MessageKind.Welcome, welcome.Kind);
        Assert.False(string.IsNullOrEmpty(welcome.SessionId));
        output.WriteLine($"brokerPid={broker.ProcessId}; partialHandshakeClosedAfterMs={started.ElapsedMilliseconds}; healthyWelcome=true");
        await broker.CloseHostAndWaitForExitAsync();
    }

    [Fact]
    public async Task Closing_host_control_reaps_broker_and_releases_pending_service_handshake()
    {
        await using var broker = await BrokerFixture.StartAsync(output, acknowledgeWelcomes: false);
        await using var service = await broker.ConnectServiceAsync();
        var waiting = broker.ExchangeAsync(service, broker.Hello());
        await broker.WelcomeForwarded.WaitAsync(broker.Token);
        await broker.CloseHostAndWaitForExitAsync();
        await Assert.ThrowsAnyAsync<IOException>(async () => await waiting);
        Assert.True(waiting.IsCompleted);
        output.WriteLine($"brokerPid={broker.ProcessId}; hostControlClosed=true; pendingHandshakeEnded=true");
    }

    [Fact]
    public async Task Host_exit_releases_an_in_flight_flyout_request()
    {
        await using var broker = await BrokerFixture.StartAsync(output);
        await using var service = await broker.ConnectServiceAsync();
        var welcome = await broker.ExchangeAsync(service, broker.Hello());
        Assert.Equal(MessageKind.Welcome, welcome.Kind);
        var pending = broker.ExchangeAsync(service, new ProtocolMessage
        {
            Kind = MessageKind.FlyoutRequest,
            ApplicationId = welcome.ApplicationId,
            SessionId = welcome.SessionId,
            RequestId = "awaiting-flyout",
            Flyout = new("logical-request", 1, "main", "panel", FlyoutKind.TaskbarGroup, 0)
        });
        await broker.FlyoutForwarded.WaitAsync(broker.Token);
        Assert.False(pending.IsCompleted);
        await broker.CloseHostAndWaitForExitAsync();
        await Assert.ThrowsAnyAsync<IOException>(async () => await pending);
        Assert.True(pending.IsCompleted);
        output.WriteLine("pendingFlyoutEnded=true; brokerExited=true");
    }

    private sealed class BrokerFixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        private readonly NamedPipeServerStream control;
        private readonly string servicePipe;
        private readonly LaunchRegistration registration;
        private readonly bool acknowledgeWelcomes;
        private readonly TaskCompletionSource<bool> welcomeForwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> flyoutForwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Process? process;
        private Task? hostPump;
        private Task? stdoutDrain;
        private Task? stderrDrain;

        private BrokerFixture(ITestOutputHelper output, bool expired, bool acknowledgeWelcomes)
        {
            this.output = output;
            this.acknowledgeWelcomes = acknowledgeWelcomes;
            string run = Guid.NewGuid().ToString("N");
            HostPipe = "mtp-handshake-host-" + run;
            servicePipe = "mtp-handshake-services-" + run;
            control = new NamedPipeServerStream(HostPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            registration = new LaunchRegistration("registered", Guid.NewGuid().ToString("N"), Secret(),
                DateTimeOffset.UtcNow.AddSeconds(expired ? -1 : ProtocolLimits.TicketLifetimeSeconds));
        }

        private string HostPipe { get; }
        public int ProcessId => process!.Id;
        public bool HasExited => process!.HasExited;
        public CancellationToken Token => deadline.Token;
        public Task WelcomeForwarded => welcomeForwarded.Task;
        public Task FlyoutForwarded => flyoutForwarded.Task;

        public static async Task<BrokerFixture> StartAsync(ITestOutputHelper output, bool expired = false, bool acknowledgeWelcomes = true)
        {
            var fixture = new BrokerFixture(output, expired, acknowledgeWelcomes);
            try
            {
                string brokerPath = Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll");
                Assert.True(File.Exists(brokerPath), "Broker runtime was not copied by the build.");
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add(brokerPath);
                fixture.process = Process.Start(start) ?? throw new IOException("Broker fixture failed to start.");
                fixture.stdoutDrain = DrainAsync(fixture.process.StandardOutput);
                fixture.stderrDrain = DrainAsync(fixture.process.StandardError);
                Assert.NotEqual(Environment.ProcessId, fixture.ProcessId);
                output.WriteLine($"hostFixturePid={Environment.ProcessId}; brokerPid={fixture.ProcessId}");
                string hostTicket = Secret();
                var other = new LaunchRegistration("registered-other", Guid.NewGuid().ToString("N"), Secret(),
                    DateTimeOffset.UtcNow.AddSeconds(ProtocolLimits.TicketLifetimeSeconds));
                await LengthPrefixedJson.WriteAsync(fixture.process.StandardInput.BaseStream,
                    new BrokerLaunch(fixture.HostPipe, hostTicket, fixture.servicePipe, [fixture.registration, other]), fixture.Token);
                fixture.process.StandardInput.Close();
                await fixture.control.WaitForConnectionAsync(fixture.Token);
                var hello = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(fixture.control, fixture.Token);
                Assert.Equal(MessageKind.Hello, hello.Kind);
                Assert.Equal(ProtocolLimits.Version, hello.Version);
                // Assert without including secret values in assertion messages or TRX output.
                Assert.True(string.Equals(hostTicket, hello.Ticket, StringComparison.Ordinal), "Host launch authentication must match.");
                await LengthPrefixedJson.WriteAsync(fixture.control, new ProtocolMessage { Kind = MessageKind.Welcome }, fixture.Token);
                var ready = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(fixture.control, fixture.Token);
                Assert.Equal(MessageKind.Welcome, ready.Kind);
                Assert.Equal("broker-ready", ready.RequestId);
                fixture.hostPump = fixture.PumpHostAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public ProtocolMessage Hello() => new()
        {
            Kind = MessageKind.Hello,
            ApplicationId = registration.ApplicationId,
            StartRequestId = registration.StartRequestId,
            Ticket = registration.Ticket,
            RequestId = "handshake",
        };

        public async Task<NamedPipeClientStream> ConnectServiceAsync()
        {
            var pipe = new NamedPipeClientStream(".", servicePipe, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(Token); return pipe; }
            catch { await pipe.DisposeAsync(); throw; }
        }

        public async Task<ProtocolMessage> ExchangeAsync(Stream pipe, ProtocolMessage message)
        {
            await LengthPrefixedJson.WriteAsync(pipe, message, Token);
            return await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, Token, Timeout.InfiniteTimeSpan);
        }

        private async Task PumpHostAsync()
        {
            try
            {
                while (!Token.IsCancellationRequested)
                {
                    var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(control, Token, Timeout.InfiniteTimeSpan);
                    if (message.Kind == MessageKind.Disconnected) continue;
                    if (message.Kind == MessageKind.FlyoutRequest)
                    {
                        flyoutForwarded.TrySetResult(true);
                        continue;
                    }
                    Assert.Equal(MessageKind.Welcome, message.Kind);
                    welcomeForwarded.TrySetResult(true);
                    if (acknowledgeWelcomes)
                        await LengthPrefixedJson.WriteAsync(control, new ProtocolMessage
                        {
                            Kind = MessageKind.Result,
                            RequestId = message.RequestId,
                            Result = ProtocolResult.Success(),
                        }, Token);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }

        public async Task CloseHostAndWaitForExitAsync()
        {
            await control.DisposeAsync();
            using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            stopDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            await process!.WaitForExitAsync(stopDeadline.Token);
            Assert.True(process.HasExited);
            await Task.WhenAll(stdoutDrain!, stderrDrain!).WaitAsync(stopDeadline.Token);
            if (hostPump is not null) await hostPump.WaitAsync(stopDeadline.Token);
            output.WriteLine($"brokerPid={ProcessId}; hostControlClosureExitCode={process.ExitCode}; processExited=true; drainsCompleted=true");
        }

        public async ValueTask DisposeAsync()
        {
            await deadline.CancelAsync();
            await control.DisposeAsync();
            try
            {
                if (process is not null)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await process.WaitForExitAsync(cleanup.Token);
                    if (stdoutDrain is not null && stderrDrain is not null)
                        await Task.WhenAll(stdoutDrain, stderrDrain).WaitAsync(cleanup.Token);
                    if (hostPump is not null) await hostPump.WaitAsync(cleanup.Token);
                }
            }
            finally { process?.Dispose(); deadline.Dispose(); }
        }

        private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        private static async Task DrainAsync(StreamReader reader)
        {
            char[] buffer = new char[1024];
            try { while (await reader.ReadAsync(buffer) != 0) { } }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }
}
