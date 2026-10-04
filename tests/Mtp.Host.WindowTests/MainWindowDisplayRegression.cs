using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mtp.Platform.Core;
using System.Reflection;

namespace Mtp.Host.WindowTests;

internal static class MainWindowDisplayRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        log("Creating isolated MainWindow regression.");
        var directory = Path.Combine(AppContext.BaseDirectory, "display-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var display = new HostDisplayController(
            new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(directory, "display.json")));
        var loaded = display.Load();
        log($"Declaration accepted: {loaded.Accepted}");
        if (!loaded.Accepted) throw new InvalidOperationException("Test declaration was not loaded.");
        var environment = new Win32TaskbarDockEnvironment();
        var store = new TestPreferenceStore(Path.Combine(directory, "dock.json"));
        using var ownedTarget = new OwnedIslandTarget();
        var dock = new HostConsoleController(display, loaded, store, ownedTarget.Capture, Path.Combine(directory, "evidence"));
        log("Creating production MainWindow.");
        var window = new MainWindow(dock, environment.GetDisplays);
        log("MainWindow created.");
        try
        {
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.AppWindow.Show(false);
            await Task.Delay(250);
            var combo = (ComboBox)((FrameworkElement)window.Content).FindName("TargetDisplayCombo");
            var original = combo.Items.Cast<ComboBoxItem>().ToArray();
            if (original.Length < 2) throw new InvalidOperationException("No target display was enumerated.");
            combo.SelectionChanged += (_, args) => log($"selection event: added={args.AddedItems.Count}, removed={args.RemovedItems.Count}, selected={combo.SelectedIndex}, count={combo.Items.Count}");
            combo.DropDownOpened += (_, _) => log("dropdown opened");
            combo.DropDownClosed += (_, _) => log("dropdown closed");
            log($"Starting production MainWindow selection with dock closed, {original.Length} choices.");
            combo.IsDropDownOpen = true;
            await Task.Delay(150);
            var target = original[^1];
            log($"Select: {target.Tag}");
            combo.SelectedItem = target;
            log("Select returned");
            combo.IsDropDownOpen = false;
            await Task.Delay(250);
            if (combo.Items.Count != original.Length || !original.SequenceEqual(combo.Items.Cast<ComboBoxItem>()))
                throw new InvalidOperationException("Selecting a display replaced the active ComboBox items.");
            if (!ReferenceEquals(combo.SelectedItem, target) || store.Load().Value?.TargetDisplayId != target.Tag as string)
                throw new InvalidOperationException("Selected display and persisted target disagree.");
            log("PASS: display selection keeps item identity and persists the target.");

            var toggle = (ToggleSwitch)((FrameworkElement)window.Content).FindName("VisibilityToggle");
            foreach (var visible in new[] { false, true })
            {
                toggle.IsOn = visible;
                await Task.Delay(100);
                if ((dock.Session.State == IslandDisplayState.Embedded) != visible) throw new InvalidOperationException("Test component visibility was not applied.");
                for (var round = 0; round < 2; round++)
                {
                    for (var index = 0; index < original.Length; index++)
                    {
                        var before = store.DisplayCommits;
                        combo.IsDropDownOpen = true;
                        await Task.Delay(75);
                        combo.SelectedItem = original[index];
                        await Task.Delay(75);
                        AssertItems(combo, original);
                        combo.IsDropDownOpen = false;
                        await Task.Delay(75);
                        AssertItems(combo, original);
                        if (!ReferenceEquals(combo.SelectedItem, original[index]) ||
                            store.Load().Value?.TargetDisplayId != original[index].Tag as string ||
                            store.DisplayCommits != before + 1)
                            throw new InvalidOperationException("Selection did not persist exactly once.");
                    }
                }
                log($"PASS: {original.Length * 2} display transitions with component visible={visible}.");
            }

            // A failed save must restore the committed selection after the selection event unwinds.
            var committed = combo.SelectedItem;
            var persisted = store.Load().Value;
            store.RejectDisplayCommit = true;
            combo.SelectedItem = original[0];
            await Task.Delay(150);
            AssertItems(combo, original);
            if (!ReferenceEquals(combo.SelectedItem, committed) || store.Load().Value != persisted || dock.PreferenceError is null)
                throw new InvalidOperationException("Failed display save did not restore the committed selection.");
            log("PASS: failed save restores selection without changing persisted preferences.");
            store.RejectDisplayCommit = false;

            toggle.IsOn = false;
            dock.SetDisplay("missing-test-display");
            if (dock.PreferenceError is not null)
                throw new InvalidOperationException("Could not stage unavailable display preference.");
            dock.Refresh();
            var unavailableItems = combo.Items.Cast<ComboBoxItem>().ToArray();
            if (combo.SelectedItem is not ComboBoxItem missing || missing.Tag as string != "missing-test-display" ||
                !missing.Content.ToString()!.Contains("暂时不可用"))
                throw new InvalidOperationException("Unavailable display preference was not retained in the list.");
            combo.IsDropDownOpen = true;
            await Task.Delay(100);
            var next = unavailableItems[1];
            combo.SelectedItem = next;
            // Cross the production one-second refresh while the popup still owns its containers.
            await Task.Delay(1200);
            AssertItems(combo, unavailableItems);
            if (!combo.IsDropDownOpen) throw new InvalidOperationException("Dropdown closed before refresh was exercised.");
            combo.IsDropDownOpen = false;
            await Task.Delay(150);
            if (combo.Items.Count != original.Length || combo.SelectedItem is not ComboBoxItem selected ||
                !Equals(selected.Tag, next.Tag) || store.Load().Value?.TargetDisplayId != next.Tag as string)
                throw new InvalidOperationException("Unavailable option was not reconciled after closing the popup.");
            log("PASS: unavailable target remains stable while open; timer and deferred refresh reconcile after closing.");
        }
        finally { window.Close(); }
    }

    private static T CreateHost<T>(string name, params object?[] arguments) =>
        (T)Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("Mtp.Host." + name, throwOnError: true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, arguments, null)!;

    private static void AssertItems(ComboBox combo, ComboBoxItem[] expected)
    {
        if (combo.Items.Count != expected.Length || !expected.SequenceEqual(combo.Items.Cast<ComboBoxItem>()))
            throw new InvalidOperationException("Display interaction replaced active ComboBox containers.");
    }

    private sealed class TestPreferenceStore(string path) : ITaskbarDockPreferenceStore
    {
        private readonly LocalTaskbarDockPreferenceStore inner = new(path);
        public bool RejectDisplayCommit { get; set; }
        public int DisplayCommits { get; private set; }
        public CoreResult<TaskbarDockPreferences> Load() => inner.Load();
        public CoreResult<TaskbarDockPreferences> CommitGap(int gapDip) => inner.CommitGap(gapDip);
        public CoreResult<TaskbarDockPreferences> CommitDisplay(string? displayId)
        {
            DisplayCommits++;
            return RejectDisplayCommit
                ? CoreResult<TaskbarDockPreferences>.Failure(new("test_save_failed", "Injected preference write failure."))
                : inner.CommitDisplay(displayId);
        }
    }
}

