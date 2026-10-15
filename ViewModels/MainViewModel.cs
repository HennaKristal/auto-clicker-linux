using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Models;
using AutoClicker.Services;
using Avalonia.Threading;

namespace AutoClicker.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const string PermissionWarning = "Permissions not granted";
    private readonly IDesktopBackend backend;
    private readonly IAppInteractions interactions;
    private readonly AppStore store;
    private readonly AutomationRunner runner = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<MacroViewModel> macros = new();
    private readonly List<AsyncCommand> allCommands = new();
    private readonly object statisticsGate = new();
    private readonly object progressGate = new();
    private ClickerSettings lastValidSettings;
    private UsageStatistics statistics;
    private AutomationController? controller;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? pendingSave;
    private Task saveTask = Task.CompletedTask;
    private IReadOnlyList<RecordedInput>? pendingRecording;
    private MacroRecording? activeRecording;
    private bool busy;
    private bool configuring;
    private bool recordingPermissionsRequired;
    private bool closing;
    private bool dirty;
    private bool loading = true;
    private long editVersion;
    private int holdPressed;
    private long lastUiUpdate;
    private AutomationState? lastQueuedState;
    private long lastQueuedGeneration;
    private RunProgress progress = new(AutomationState.Idle, 0, 0, PermissionWarning);
    private string sort = "";
    private bool descending;
    public ClickerSettingsViewModel Settings { get; private set; }
    public ObservableCollection<MacroViewModel> VisibleMacros { get; } = new();
    public bool RecordClicks { get => Get(true); set { Put(value); ScheduleSave(); RefreshRecordingPermissions(); } }
    public bool RecordKeyboard { get => Get(true); set { Put(value); ScheduleSave(); RefreshRecordingPermissions(); } }
    public string Search { get => Get(""); set { Put(value); RefreshMacros(); } }
    public string ErrorText { get => Get(""); private set => Put(value); }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public string StorageMessage { get => Get(""); private set => Put(value); }
    public bool HasStorageMessage => !string.IsNullOrWhiteSpace(StorageMessage);
    // An authorized idle session needs no footer message; activity and errors still do.
    public string Status { get => Get(PermissionWarning); private set => Put(value == "Ready" ? "" : value); }
    public bool HasPermissionWarning => Status == PermissionWarning;
    public bool BackendReady => backend.Ready;
    public bool SettingsEditable => !busy && !configuring && !closing && progress.State == AutomationState.Idle;
    public bool CanStart => !busy && !configuring && !closing && controller?.Progress.State is not (AutomationState.PlayingMacro or AutomationState.Stopping);
    public bool CanStop => !busy && !configuring && !closing && (controller?.Progress.State is AutomationState.Starting or AutomationState.Clicking or AutomationState.PlayingMacro);
    public bool CanRecord => backend.Ready && !busy && !configuring && !closing;
    public bool CanManageMacros => SettingsEditable;
    public bool CanReorder => SettingsEditable && sort == "" && string.IsNullOrWhiteSpace(Search);
    public bool NoVisibleMacros => VisibleMacros.Count == 0;
    public string EmptyMacroText => string.IsNullOrWhiteSpace(Search) ? "No macros yet. Record your actions to create one." : "No macros match your search.";
    public Guid? ActiveMacroId => progress.MacroId;
    public string StartLabel => !backend.Ready ? "Allow permissions" : progress.State == AutomationState.Clicking || progress.State == AutomationState.Starting ? "Stop" : "Start";
    public string RecordLabel => progress.State == AutomationState.Recording ? "Stop recording" : recordingPermissionsRequired ? "Allow recording" : "Record";
    public string RecordDescription => progress.State == AutomationState.Recording ? "Click again to finish and save" : recordingPermissionsRequired ? "Approve access in the system prompt" : "Start recording your actions";
    public string ConfigureLabel => configuring ? "Done editing hotkeys" : "Edit hotkeys";
    public string ActivationHotkey => Trigger("activate");
    public string RecordStartHotkey => Trigger("record-start");
    public string RecordStopHotkey => Trigger("record-stop");
    public string Version => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0");
    public string TotalClicks { get { lock (statisticsGate) { return (statistics.Clicks + (progress.State is AutomationState.Clicking or AutomationState.Starting ? progress.Clicks : 0)).ToString("N0"); } } }
    public string TotalTime { get { lock (statisticsGate) { return TimeSpan.FromMilliseconds(statistics.RunningMs).ToString(@"d\d\ hh\h\ mm\m\ ss\s"); } } }
    public string Sessions { get { lock (statisticsGate) { return statistics.Sessions.ToString("N0"); } } }
    public string RateFeedback
    {
        get
        {
            try
            {
                var settings = Settings.Build();
                var hold = settings.HoldDurationMs(settings.IntervalMs);
                var gesture = hold * (settings.DoubleClick ? 2 : 1) + (settings.DoubleClick ? settings.DoubleDelayMs : 0);
                var feedback = gesture > settings.IntervalMs ? "Gesture duration limits speed to about " + (1000 / gesture).ToString("0.##") + " cycles/s." : "Requested cycle interval: " + settings.IntervalMs.ToString("0.##") + " ms.";
                return progress.State == AutomationState.Clicking && progress.ElapsedMs > 0 ? "Average: " + (progress.Clicks * 1000d / progress.ElapsedMs).ToString("N0") + " clicks/s. " + feedback : feedback;
            }
            catch (ArgumentException error) { return error.Message; }
        }
    }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand RecordCommand { get; }
    public AsyncCommand ConfigureCommand { get; }
    public AsyncCommand PickCommand { get; }
    public AsyncCommand PlayMacroCommand { get; }
    public AsyncCommand EditMacroCommand { get; }
    public AsyncCommand DeleteMacroCommand { get; }
    public AsyncCommand SortCommand { get; }
    public AsyncCommand ResetCommand { get; }

    public MainViewModel(IDesktopBackend backend, IAppInteractions interactions, AppStore store)
    {
        this.backend = backend;
        this.interactions = interactions;
        this.store = store;
        var loaded = store.Load();
        statistics = loaded.Data.Statistics;
        lastValidSettings = loaded.Configuration.Clicker;
        Settings = new(loaded.Configuration.Clicker);
        Settings.PropertyChanged += OnSettingsChanged;
        RecordClicks = loaded.Configuration.RecordClicks;
        RecordKeyboard = loaded.Configuration.RecordKeyboard;
        StorageMessage = loaded.Warning ?? "";
        foreach (var macro in loaded.Data.Macros) { macros.Add(new(this, macro)); }
        StartCommand = Command(_ => StartClickerAsync(true), () => CanStart);
        StopCommand = Command(_ => StopPlaybackAsync(), () => CanStop);
        RecordCommand = Command(_ => ToggleRecordingAsync(), () => CanRecord);
        ConfigureCommand = Command(_ => ConfigureAsync(), () => backend.Ready && !busy && !closing);
        PickCommand = Command(_ => PickAsync(), () => SettingsEditable && backend.Ready && Settings.PositionEnabled);
        PlayMacroCommand = Command(item => PlayMacroAsync((MacroViewModel)item!), () => backend.Ready && !busy && !configuring && !closing);
        EditMacroCommand = Command(item => EditMacroAsync((MacroViewModel)item!), () => CanManageMacros);
        DeleteMacroCommand = Command(item => DeleteMacroAsync((MacroViewModel)item!), () => CanManageMacros);
        SortCommand = Command(key => { var selected = (string?)key ?? ""; descending = sort == selected && !descending; sort = sort == selected && descending == false ? "" : selected; RefreshMacros(); return Task.CompletedTask; }, () => !closing);
        ResetCommand = Command(_ => ResetAsync(), () => !busy && !closing);
        backend.Shortcut += OnShortcut;
        backend.BindingsChanged += () => Ui(Refresh);
        backend.Lost += error => { controller?.Cancel(); operation?.Cancel(); Ui(() => { ErrorText = error.Message; Status = PermissionWarning; Refresh(); }); };
        RefreshMacros();
        loading = false;
        dirty = false;
    }

    private AsyncCommand Command(Func<object?, Task> action, Func<bool> allowed)
    {
        var command = new AsyncCommand(async parameter =>
        {
            if (closing) { return; }
            await commands.WaitAsync(lifetime.Token);
            try
            {
                if (closing || !allowed()) { return; }
                ErrorText = "";
                await action(parameter);
            }
            finally { commands.Release(); Refresh(); }
        }, allowed, ReportError);
        allCommands.Add(command);
        return command;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == "") { return; }
        ScheduleSave();
        if (args.PropertyName == nameof(ClickerSettingsViewModel.PositionEnabled)) { PickCommand.Refresh(); }
        Raise();
    }

    private static void Ui(Action action) { if (Dispatcher.UIThread.CheckAccess()) { action(); } else { Dispatcher.UIThread.Post(action); } }
    private void ReportError(Exception error) { ErrorText = error.Message; Status = backend.Ready ? "Ready" : PermissionWarning; RefreshRecordingPermissions(); Refresh(); }
    private void RefreshRecordingPermissions() { recordingPermissionsRequired = backend.Ready && !backend.HasRecordingPermissions(RecordClicks, RecordKeyboard); Raise(); }
    public string Trigger(string id) => backend.Bindings.FirstOrDefault(binding => binding.Id == id) is { IsAssigned: true } binding ? binding.TriggerDescription : "Unassigned";
    private IReadOnlyList<ShortcutDefinition> Definitions() => new[] { new ShortcutDefinition("activate", "Toggle / hold clicking", "NUM+1"), new ShortcutDefinition("record-start", "Start recording", "NUM+2"), new ShortcutDefinition("record-stop", "Stop recording", "NUM+3") }.Concat(macros.Select(macro => new ShortcutDefinition("macro_" + macro.Id.ToString("N"), "Play / stop: " + macro.Name))).ToArray();

    private async Task ConnectAsync()
    {
        busy = true;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Status = "Waiting for system authorization";
        Refresh();
        try
        {
            await DisposeControllerAsync();
            await backend.ConnectAsync(Definitions(), text => Ui(() => Status = text), operation.Token);
            controller = new(backend.Playback);
            controller.Changed += OnProgress;
            controller.Finished += OnFinished;
            RefreshRecordingPermissions();
            Status = "Ready";
        }
        finally { busy = false; operation.Dispose(); operation = null; Refresh(); }
    }

    private async Task DisposeControllerAsync() { if (controller != null) { await controller.DisposeAsync(); controller = null; } }

    private async Task StartClickerAsync(bool countdown)
    {
        if (!CanStart) { return; }
        if (!backend.Ready) { await ConnectAsync(); return; }
        if (progress.State is AutomationState.Clicking or AutomationState.Starting) { await StopActivityAsync(); return; }
        await StopActivityAsync();
        var settings = Settings.Build();
        if (!countdown && settings.Activation == ActivationMode.Hold && Volatile.Read(ref holdPressed) == 0) { return; }
        lock (statisticsGate) { statistics = statistics with { Sessions = statistics.Sessions + 1 }; }
        ScheduleSave();
        await controller!.StartAsync(countdown ? AutomationState.Starting : AutomationState.Clicking, async token =>
        {
            if (countdown) { Ui(() => Status = "Starting in 2 seconds — focus your target"); await Task.Delay(2000, token); }
            controller.MarkRunning();
            return await runner.ClickAsync(settings, backend.Playback, () => backend.Snapshot, controller.Report, token);
        });
    }

    private async Task ToggleRecordingAsync()
    {
        if (progress.State == AutomationState.Recording) { await StopActivityAsync(); return; }
        await StopActivityAsync();
        if (!RecordClicks && !RecordKeyboard) { throw new ArgumentException("Enable mouse clicks or keyboard input before recording."); }
        if (recordingPermissionsRequired) { await AllowRecordingAsync(); return; }
        var recordClicks = RecordClicks;
        var recordKeyboard = RecordKeyboard;
        await controller!.StartAsync(AutomationState.Recording, async token =>
        {
            var clock = new MonotonicClock();
            var started = clock.NowMs;
            var capture = await backend.StartCaptureAsync(recordClicks, recordKeyboard, token);
            var recording = new MacroRecording(capture.StartedMicroseconds, recordClicks, recordKeyboard);
            activeRecording = recording;
            try
            {
                capture.Input += input => { var desktop = backend.Snapshot; recording.Add(input, desktop?.Cursor, desktop?.ActiveWindow?.ProcessId == Environment.ProcessId); controller.Report(recording.Count, clock.NowMs - started); };
                try { await capture.Completion.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                await capture.DisposeAsync();
                pendingRecording = recording.Finish(clock.NowMs - started, backend.Bindings);
                return new(0, clock.NowMs - started, "Recording stopped");
            }
            finally { activeRecording = null; await capture.DisposeAsync(); }
        });
    }

    private async Task AllowRecordingAsync()
    {
        busy = true;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Status = "Waiting for recording authorization";
        Refresh();
        try { await backend.GrantRecordingPermissionsAsync(RecordClicks, RecordKeyboard, operation.Token); }
        finally
        {
            busy = false;
            operation.Dispose();
            operation = null;
            RefreshRecordingPermissions();
            Status = backend.Ready ? "Ready" : PermissionWarning;
            Refresh();
        }
    }

    // Recheck after queued commands before stopping playback.
    private Task StopPlaybackAsync() => CanStop ? StopActivityAsync() : Task.CompletedTask;

    private async Task StopActivityAsync()
    {
        operation?.Cancel();
        configuring = false;
        if (controller != null) { await controller.StopAsync(); progress = controller.Progress; Status = progress.Error == null ? progress.Message : backend.Ready ? "Ready" : PermissionWarning; }
        else { Status = backend.Ready ? "Ready" : PermissionWarning; }
        await SaveRecordingAsync();
        Refresh();
    }

    private async Task SaveRecordingAsync(bool allowClosing = false)
    {
        var recording = pendingRecording;
        pendingRecording = null;
        if (recording == null || recording.Count == 0 || closing && !allowClosing) { return; }
        if (macros.Count >= 1000 || macros.Sum(macro => macro.EventCount) + recording.Count > 200_000) { throw new InvalidOperationException("The macro library reached its limit. Delete unused recordings before saving another."); }
        busy = true;
        Refresh();
        string? name;
        try { name = await interactions.NameRecordingAsync(); }
        finally { busy = false; Refresh(); }
        if (string.IsNullOrWhiteSpace(name)) { Status = "Recording discarded"; return; }
        var definition = new MacroDefinition(Guid.NewGuid(), name.Trim(), DateTimeOffset.Now, recording);
        MacroValidation.Validate(definition);
        macros.Add(new(this, definition));
        RefreshMacros();
        await SaveNowAsync();
        if (backend.Ready && !closing) { await UpdateShortcutsAsync(); }
        Status = "Saved: " + definition.Name;
    }

    private async Task PlayMacroAsync(MacroViewModel macro)
    {
        if (progress.State == AutomationState.PlayingMacro && progress.MacroId == macro.Id) { await StopActivityAsync(); return; }
        await StopActivityAsync();
        var definition = macro.Build();
        MacroValidation.Validate(definition);
        await controller!.StartAsync(AutomationState.PlayingMacro, token => runner.PlayAsync(definition, backend.Playback, controller.Report, token), definition.Id);
    }

    private async Task EditMacroAsync(MacroViewModel macro)
    {
        await StopActivityAsync();
        busy = true;
        Refresh();
        MacroDefinition? edited;
        try { edited = await interactions.EditMacroAsync(macro.Build()); }
        finally { busy = false; Refresh(); }
        if (edited == null) { return; }
        MacroValidation.Validate(edited);
        macros[macros.IndexOf(macro)] = new(this, edited);
        RefreshMacros();
        await SaveNowAsync();
        if (backend.Ready) { await UpdateShortcutsAsync(); }
    }

    private async Task DeleteMacroAsync(MacroViewModel macro)
    {
        if (!await ConfirmAsync("Delete macro", "Delete “" + macro.Name + "”?")) { return; }
        macros.Remove(macro);
        RefreshMacros();
        await SaveNowAsync();
        if (backend.Ready) { await UpdateShortcutsAsync(); }
    }

    private async Task ConfigureAsync()
    {
        if (configuring) { configuring = false; Status = "Ready"; return; }
        await StopActivityAsync();
        configuring = true;
        Status = "";
        Refresh();
        try { await backend.ConfigureShortcutsAsync(lifetime.Token); }
        catch { configuring = false; throw; }
    }

    private async Task UpdateShortcutsAsync(bool reset = false)
    {
        busy = true;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Status = "Waiting for shortcut authorization";
        Refresh();
        try { if (reset) { await backend.ResetShortcutsAsync(Definitions(), operation.Token); } else { await backend.BindShortcutsAsync(Definitions(), operation.Token); } }
        finally { busy = false; operation.Dispose(); operation = null; Status = backend.Ready ? "Ready" : PermissionWarning; Refresh(); }
    }

    public async Task ConfigureFromDialogAsync()
    {
        if (!backend.Ready) { return; }
        configuring = true;
        Status = "";
        Refresh();
        try { await backend.ConfigureShortcutsAsync(lifetime.Token); }
        catch (Exception error) { configuring = false; ReportError(error); }
    }

    private async Task PickAsync()
    {
        busy = true;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Status = "Click the desired position; Escape cancels";
        Refresh();
        try { if (await interactions.PickPositionAsync(operation.Token) is { } point) { Settings.PositionX = (decimal)point.X; Settings.PositionY = (decimal)point.Y; } }
        finally { busy = false; operation.Dispose(); operation = null; Status = "Ready"; Refresh(); }
    }

    private async Task ResetAsync()
    {
        await StopActivityAsync();
        if (!await ConfirmAsync("Reset settings", "Restore clicker, recording options, and activation/recording shortcuts to their defaults? Saved macros, their play shortcuts, and lifetime statistics are preserved.")) { return; }
        Settings.PropertyChanged -= OnSettingsChanged;
        Settings = new(new ClickerSettings());
        Settings.PropertyChanged += OnSettingsChanged;
        RecordClicks = true;
        RecordKeyboard = true;
        ScheduleSave();
        await SaveNowAsync();
        await UpdateShortcutsAsync(reset: true);
        Raise();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        busy = true;
        Refresh();
        try { return await interactions.ConfirmAsync(title, message); }
        finally { busy = false; Refresh(); }
    }

    private void OnShortcut(ShortcutEvent input)
    {
        if (input.Pressed) { activeRecording?.SuppressControl(Trigger(input.Id), new MonotonicClock().NowMs); }
        if (input.Id == "activate")
        {
            Volatile.Write(ref holdPressed, input.Pressed ? 1 : 0);
            if (!input.Pressed && Settings.Activation == ActivationMode.Hold && controller?.Progress.State is AutomationState.Clicking or AutomationState.Starting) { controller?.Cancel(); }
        }
        if (!busy && !configuring) { Ui(() => _ = HandleShortcutAsync(input)); }
    }

    private async Task HandleShortcutAsync(ShortcutEvent input)
    {
        if (closing) { return; }
        try
        {
            await commands.WaitAsync(lifetime.Token);
            try
            {
                if (!backend.Ready || busy || configuring) { return; }
                if (input.Id == "activate")
                {
                    if (input.Pressed) { if (Settings.Activation != ActivationMode.Hold || progress.State != AutomationState.Clicking) { await StartClickerAsync(false); } }
                    else if (Settings.Activation == ActivationMode.Hold && progress.State is AutomationState.Clicking or AutomationState.Starting) { await StopActivityAsync(); }
                }
                else if (input.Pressed && input.Id == "record-start" && progress.State != AutomationState.Recording) { await ToggleRecordingAsync(); }
                else if (input.Pressed && input.Id == "record-stop" && progress.State == AutomationState.Recording) { await StopActivityAsync(); }
                else if (input.Pressed && macros.FirstOrDefault(macro => "macro_" + macro.Id.ToString("N") == input.Id) is { } macro) { await PlayMacroAsync(macro); }
            }
            finally { commands.Release(); Refresh(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportError(error); }
    }

    private void OnProgress(RunProgress update)
    {
        // Compare queued states, since the UI may not have displayed the preceding update yet.
        var now = Environment.TickCount64;
        lock (progressGate)
        {
            if (update.State == lastQueuedState && update.Generation == lastQueuedGeneration && update.Error == null && now - lastUiUpdate < 75) { return; }
            lastQueuedState = update.State;
            lastQueuedGeneration = update.Generation;
            lastUiUpdate = now;
        }
        Ui(() =>
        {
            if (controller == null || controller.Progress.Generation != update.Generation || ReferenceEquals(progress, update)) { return; }
            progress = update;
            if (update.Error != null) { ReportError(update.Error); }
            else
            {
                Status = update.State switch
                {
                    AutomationState.Recording => "Recording - " + update.Clicks + " events",
                    AutomationState.Clicking or AutomationState.PlayingMacro => update.Message + " - " + update.Clicks + " clicks",
                    _ => update.Message
                };
                Refresh();
            }
            if (update.State == AutomationState.Idle && pendingRecording != null && !closing) { _ = FinishAutomaticRecordingAsync(); }
        });
    }

    private async Task FinishAutomaticRecordingAsync()
    {
        await commands.WaitAsync();
        try { await SaveRecordingAsync(); }
        catch (Exception error) { ReportError(error); }
        finally { commands.Release(); }
    }

    private void OnFinished(AutomationState state, RunResult result)
    {
        if (state is AutomationState.Clicking or AutomationState.Starting) { lock (statisticsGate) { statistics = statistics with { Clicks = statistics.Clicks + result.Clicks, RunningMs = statistics.RunningMs + result.ElapsedMs }; } dirty = true; Interlocked.Increment(ref editVersion); Ui(ScheduleSave); }
    }

    private void Refresh()
    {
        Raise();
        foreach (var command in allCommands) { command.Refresh(); }
        foreach (var macro in macros) { macro.Raise(); }
    }

    private void RefreshMacros()
    {
        IEnumerable<MacroViewModel> items = macros.Where(macro => macro.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
        Func<MacroViewModel, object> key = sort switch { "Name" => macro => macro.Name, "Hotkey" => macro => macro.Hotkey, "Repeat" => macro => macro.Repeat ?? 0, "Length" => macro => macro.DurationMs, "Created" => macro => macro.CreatedAt, _ => macro => macros.IndexOf(macro) };
        if (sort != "") { items = descending ? items.OrderByDescending(key) : items.OrderBy(key); }
        VisibleMacros.Clear();
        foreach (var macro in items) { VisibleMacros.Add(macro); }
        Raise();
    }

    public void Reorder(Guid source, Guid target)
    {
        if (!CanReorder) { return; }
        var item = macros.FirstOrDefault(macro => macro.Id == source);
        var destination = macros.FindIndex(macro => macro.Id == target);
        if (item == null || destination < 0) { return; }
        macros.Remove(item);
        macros.Insert(destination, item);
        RefreshMacros();
        ScheduleSave();
    }

    public void MacroChanged() { ScheduleSave(); }
    private void ScheduleSave()
    {
        if (closing || loading) { return; }
        dirty = true;
        Interlocked.Increment(ref editVersion);
        pendingSave?.Cancel();
        pendingSave?.Dispose();
        pendingSave = new();
        saveTask = SaveLaterAsync(pendingSave.Token);
    }

    private async Task SaveLaterAsync(CancellationToken token)
    {
        try { await Task.Delay(700, token); await SaveNowAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { StorageMessage = "Could not save: " + error.Message; Raise(); }
    }

    private async Task SaveNowAsync()
    {
        var savingVersion = editVersion;
        string? validation = null;
        try { lastValidSettings = Settings.Build(); }
        catch (ArgumentException error) { validation = "Settings were not saved: " + error.Message; }
        var config = new AppConfiguration(1, lastValidSettings, RecordClicks, RecordKeyboard);
        AppData data;
        lock (statisticsGate) { data = new(1, macros.Select(macro => macro.Build()).ToArray(), statistics); }
        await store.SaveAsync(config, data);
        dirty = editVersion != savingVersion || validation != null;
        StorageMessage = validation ?? "";
        Raise(nameof(StorageMessage));
    }

    public void CancelPending() { controller?.Cancel(); operation?.Cancel(); }
    public void NoteApplicationPress(uint code) => activeRecording?.NoteApplicationPress(code, new MonotonicClock().NowMs);
    public async Task ShutdownAsync()
    {
        if (closing) { return; }
        closing = true;
        lifetime.Cancel();
        pendingSave?.Cancel();
        CancelPending();
        await commands.WaitAsync();
        try
        {
            if (controller != null) { await controller.StopAsync(); }
            try { await SaveRecordingAsync(allowClosing: true); }
            catch (Exception error) { StorageMessage = "Could not save: " + error.Message; }
            await DisposeControllerAsync();
            await saveTask;
            try { if (dirty) { await SaveNowAsync(); } }
            catch (Exception error) { StorageMessage = "Could not save: " + error.Message; }
        }
        finally { try { runner.Dispose(); await backend.DisposeAsync(); } finally { commands.Release(); } }
    }
}
