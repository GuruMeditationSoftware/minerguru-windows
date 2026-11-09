using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core.Models;

namespace MinerGuru.Dialogs;

/// <summary>Create or edit a maintenance task.</summary>
public sealed partial class MaintenanceTaskDialog : ContentDialog
{
    /// <summary>The saved task once the dialog was confirmed.</summary>
    public MaintenanceTask? Result { get; private set; }

    public MaintenanceTaskDialog(MaintenanceTask task, bool isNew, IReadOnlyList<MinerDevice> miners)
    {
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 600.0;
        PrimaryButtonText = Strings.SaveEntry;
        CloseButtonText = Strings.Cancel;
        DefaultButton = ContentDialogButton.Primary;

        var ids = new HashSet<Guid>(task.MinerIds);
        var title = new TextBox { Text = task.CustomTitle, PlaceholderText = task.Kind == MaintenanceKind.Custom ? "" : task.Kind.Title() };
        var due = new CalendarDatePicker { Date = task.NextDue, HorizontalAlignment = HorizontalAlignment.Left };
        var interval = new NumberBox { Value = task.IntervalDays, Minimum = 0, Maximum = 3650, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Left };
        var notes = new TextBox { Text = task.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };

        var form = new StackPanel { Width = 520, Spacing = 14 };
        form.Children.Add(PageParts.Title(isNew ? Strings.AddMaintenanceTask : Strings.EditMaintenanceTask));
        form.Children.Add(PowerForm.Row(Strings.Title, title));
        if (task.Kind.Hint() is { Length: > 0 } hint) form.Children.Add(PowerForm.Note(hint));
        form.Children.Add(PowerForm.Row(Strings.DueOn, due));
        form.Children.Add(PowerForm.Row(Strings.EveryDays, interval));
        form.Children.Add(PowerForm.Note(Strings.ZeroDaysOneTimeTask));
        form.Children.Add(PowerForm.Row("Miner", new MinerScopePicker(ids, miners)));
        form.Children.Add(PowerForm.Row(Strings.Note, notes));
        Content = new ScrollViewer { Content = form, MaxHeight = 560 };

        void Validate() => IsPrimaryButtonEnabled = task.Kind != MaintenanceKind.Custom || title.Text.Trim().Length > 0;
        title.TextChanged += (_, _) => Validate();
        Validate();
        PrimaryButtonClick += (_, _) =>
        {
            task.CustomTitle = title.Text.Trim();
            task.NextDue = (due.Date ?? DateTimeOffset.Now).Date;
            task.IntervalDays = double.IsNaN(interval.Value) ? 0 : (int)interval.Value;
            task.MinerIds = ids;
            task.Notes = notes.Text.Trim();
            Result = task;
        };
    }
}

/// <summary>Marks a task as done on a date, with a note for the record.</summary>
public sealed partial class MaintenanceDoneDialog : ContentDialog
{
    public DateTime Date { get; private set; } = DateTime.Now;
    public string Notes { get; private set; } = "";

    public MaintenanceDoneDialog(MaintenanceTask task)
    {
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 500.0;
        PrimaryButtonText = Strings.Completed;
        CloseButtonText = Strings.Cancel;
        DefaultButton = ContentDialogButton.Primary;
        var date = new CalendarDatePicker { Date = DateTimeOffset.Now, HorizontalAlignment = HorizontalAlignment.Left };
        var notes = new TextBox { PlaceholderText = Strings.WhatWasDone, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        var form = new StackPanel { Width = 420, Spacing = 14 };
        form.Children.Add(PageParts.Title(task.Title));
        form.Children.Add(PowerForm.Row(Strings.Date, date));
        form.Children.Add(PowerForm.Row(Strings.Note, notes));
        Content = form;
        PrimaryButtonClick += (_, _) =>
        {
            Date = (date.Date ?? DateTimeOffset.Now).Date;
            Notes = notes.Text.Trim();
        };
    }
}
