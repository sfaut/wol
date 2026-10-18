using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wol.Desktop.Models;
using Wol.Desktop.Services;
using Wol.Desktop.ViewModels;

namespace Wol.Desktop.Views;

/// <summary>
/// Small dialogs: new / rename group, delete group, remove machines
/// (design/specs/layout.md, "Groups"; design/specs/interaction.md, "Confirmations").
/// </summary>
internal static class Dialogs
{
    /// <summary>
    /// Asks for a group name and creates or renames the group; errors are shown in the dialog.
    /// </summary>
    /// <param name="root">XAML root the dialog is shown in.</param>
    /// <param name="viewModel">App state, which saves the group.</param>
    /// <param name="renaming">Group to rename; null to create one.</param>
    /// <returns>The name saved, or null when cancelled.</returns>
    public static async Task<string?> GroupNameAsync(XamlRoot root, MainViewModel viewModel, GroupSection? renaming)
    {
        var box = new TextBox
        {
            Header = Strings.Get("GroupDialog_Name_Header"),
            Text = renaming?.Group ?? string.Empty,
            MaxLength = 64,
        };
        var message = new StackPanel { Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            message,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        var panel = new StackPanel { Spacing = 4, Width = 360 };
        panel.Children.Add(box);
        panel.Children.Add(message);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = Strings.Get(renaming is null ? "GroupDialog_NewTitle" : "GroupDialog_RenameTitle"),
            PrimaryButtonText = Strings.Get(renaming is null ? "GroupDialog_Create" : "GroupDialog_Rename"),
            CloseButtonText = Strings.Get("GroupDialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
        };

        void Validate()
        {
            var name = box.Text.Trim();
            var existing = viewModel.GroupNames.FirstOrDefault(group =>
                string.Equals(group, name, StringComparison.OrdinalIgnoreCase));
            string? error = null;
            if (name.Length == 0)
            {
                error = null;
            }
            else if (MachineDialog.IsReservedGroup(name))
            {
                error = Strings.Format("Validation_Group_Reserved", name);
            }
            else if (existing is not null && existing != renaming?.Group)
            {
                error = Strings.Format("Validation_Group_Duplicate", existing);
            }

            MachineDialog.SetMessage(message, error is null ? MachineDialog.MessageKind.None : MachineDialog.MessageKind.Error, error);
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && error is null && name != renaming?.Group;
        }

        string? saved = null;
        box.TextChanged += (_, _) => Validate();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            var name = box.Text.Trim();
            var error = renaming is null
                ? await viewModel.AddGroupAsync(name)
                : await viewModel.RenameGroupAsync(renaming, name);
            if (error is null)
            {
                saved = name;
            }
            else
            {
                args.Cancel = true;
                MachineDialog.SetMessage(message, MachineDialog.MessageKind.Error, error);
            }
            deferral.Complete();
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        Validate();

        await dialog.ShowAsync();
        return saved;
    }

    /// <summary>
    /// Asks what to do with the machines of a group being deleted.
    /// </summary>
    /// <param name="root">XAML root the dialog is shown in.</param>
    /// <param name="section">Group to delete.</param>
    /// <returns>Null when cancelled; true to remove its machines too, false to ungroup them.</returns>
    public static async Task<bool?> DeleteGroupAsync(XamlRoot root, GroupSection section)
    {
        var count = section.Machines.Count;
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = count == 0
                ? Strings.Get("DeleteGroup_Body_Empty")
                : Strings.Plural("DeleteGroup_Body", count, section.Title, count),
        });
        var options = new RadioButtons();
        options.Items.Add(Strings.Get("DeleteGroup_Option_Move"));
        options.Items.Add(Strings.Get("DeleteGroup_Option_Delete"));
        options.SelectedIndex = 0;
        if (count > 0)
        {
            panel.Children.Add(options);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = Strings.Format("DeleteGroup_Title", section.Title),
            PrimaryButtonText = Strings.Get("DeleteGroup_Confirm"),
            CloseButtonText = Strings.Get("DeleteGroup_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = panel,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }
        return count > 0 && options.SelectedIndex == 1;
    }

    /// <summary>
    /// Asks before removing machines from Wol.
    /// </summary>
    /// <param name="root">XAML root the dialog is shown in.</param>
    /// <param name="machines">Machines to remove.</param>
    /// <returns>True when confirmed.</returns>
    public static async Task<bool> RemoveMachinesAsync(XamlRoot root, IReadOnlyList<MachineItem> machines)
    {
        var one = machines.Count == 1;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = one
                ? Strings.Format("RemoveDialog_Title_One", machines[0].Name)
                : Strings.Format("RemoveDialog_Title_Other", machines.Count),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Strings.Get(one ? "RemoveDialog_Body_One" : "RemoveDialog_Body_Other"),
            },
            PrimaryButtonText = Strings.Get("RemoveDialog_Confirm"),
            CloseButtonText = Strings.Get("RemoveDialog_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
