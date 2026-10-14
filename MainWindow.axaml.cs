using System;
using AutoClicker.Platform.Linux;
using AutoClicker.Services;
using AutoClicker.ViewModels;
using AutoClicker.Views;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AutoClicker;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly WindowInteractions interactions;
    private bool shutdownComplete;
    private bool shuttingDown;

    public MainWindow()
    {
        InitializeComponent();
        var backend = new LinuxDesktopBackend();
        interactions = new(this, () => backend.Snapshot);
        viewModel = new MainViewModel(backend, interactions, new AppStore());
        interactions.ViewModel = viewModel;
        DataContext = viewModel;
        Closing += OnClosing;
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Pointer.Type != PointerType.Mouse) { return; }
        var code = args.GetCurrentPoint(this).Properties.PointerUpdateKind switch { PointerUpdateKind.LeftButtonPressed => 0x110u, PointerUpdateKind.MiddleButtonPressed => 0x112u, PointerUpdateKind.RightButtonPressed => 0x111u, _ => 0u };
        if (code != 0) { viewModel.NoteApplicationPress(code); }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (shutdownComplete) { return; }
        args.Cancel = true;
        if (shuttingDown) { return; }
        shuttingDown = true;
        viewModel.CancelPending();
        interactions.CloseDialogs();
        try { await viewModel.ShutdownAsync(); if (viewModel.StorageMessage.StartsWith("Could not save:", StringComparison.Ordinal) || viewModel.StorageMessage.StartsWith("Settings were not saved:", StringComparison.Ordinal)) { await interactions.ShowSaveErrorAsync(viewModel.StorageMessage); } }
        catch (Exception error) { Console.Error.WriteLine("Shutdown failed: " + error.Message); }
        finally { shutdownComplete = true; Close(); }
    }
}
