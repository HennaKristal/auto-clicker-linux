using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AutoClicker.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    private readonly Dictionary<string, object?> values = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    protected T Get<T>(T fallback, [CallerMemberName] string name = "") => values.TryGetValue(name, out var value) ? (T)value! : fallback;
    protected void Put<T>(T value, [CallerMemberName] string name = "") { if (values.TryGetValue(name, out var previous) && Equals(previous, value)) { return; } values[name] = value; Raise(name); Raise(""); }
    public void Raise(string name = "") => PropertyChanged?.Invoke(this, new(name));
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Func<bool> allowed;
    private readonly Action<Exception> error;
    private bool busy;
    public event EventHandler? CanExecuteChanged;
    public AsyncCommand(Func<object?, Task> execute, Func<bool> allowed, Action<Exception> error) { this.execute = execute; this.allowed = allowed; this.error = error; }
    public bool CanExecute(object? parameter) => !busy && allowed();
    public async void Execute(object? parameter) { await ExecuteAsync(parameter); }
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) { return; }
        busy = true;
        Refresh();
        try { await execute(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception failure) { error(failure); }
        finally { busy = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
