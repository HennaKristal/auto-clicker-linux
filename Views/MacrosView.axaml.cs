using System;
using AutoClicker.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace AutoClicker.Views;

public partial class MacrosView : UserControl
{
    public MacrosView() { InitializeComponent(); }
    private async void OnDragHandlePressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is not Control { DataContext: MacroViewModel macro } || !macro.Owner.CanReorder || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { return; }
        using var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(macro.Id.ToString()));
        await DragDrop.DoDragDropAsync(args, transfer, DragDropEffects.Move);
    }
    private void OnDragOver(object? sender, DragEventArgs args) { args.DragEffects = DataContext is MainViewModel { CanReorder: true } && Guid.TryParse(args.DataTransfer.TryGetText(), out _) ? DragDropEffects.Move : DragDropEffects.None; args.Handled = true; }
    private void OnDrop(object? sender, DragEventArgs args) { if (sender is Control { DataContext: MacroViewModel target } && Guid.TryParse(args.DataTransfer.TryGetText(), out var source)) { target.Owner.Reorder(source, target.Id); } args.Handled = true; }
}
