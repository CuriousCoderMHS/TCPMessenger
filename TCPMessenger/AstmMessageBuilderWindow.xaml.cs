using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TCPMessenger;

public partial class AstmMessageBuilderWindow : Window
{
    public string GeneratedMessage { get; private set; } = string.Empty;

    public AstmMessageBuilderWindow(IEnumerable<string> existingRecords)
    {
        InitializeComponent();

        foreach (string record in existingRecords.Where(
                     record => !string.IsNullOrWhiteSpace(record)))
        {
            RecordsListBox.Items.Add(record);
        }

        ApplyRecordTemplate();
    }

    private void RecordTypeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        // This event can fire while InitializeComponent is still creating controls.
        if (!IsLoaded || RecordTextBox is null)
            return;

        ApplyRecordTemplate();
    }

    private void ApplyRecordTemplate()
    {
        if (RecordTypeComboBox is null || RecordTextBox is null)
            return;

        string recordType = (RecordTypeComboBox.SelectedItem as ComboBoxItem)?
            .Content?
            .ToString() ?? string.Empty;

        RecordTextBox.Text = recordType switch
        {
            "Header (H)" => "H|\\^&|||TCP-MESSENGER|||||P|1",
            "Patient (P)" => "P|1||TEST-001||DOE^JANE||19800101|F",
            "Order (O)" => "O|1|ORD-001||^^^GLU|||||||||SERUM",
            "Result (R)" => "R|1|^^^GLU|5.4|mmol/L|3.9-5.5|N|||F",
            "Terminator (L)" => "L|1|N",
            _ => string.Empty
        };
    }

    private void AddRecord_Click(object sender, RoutedEventArgs e)
    {
        string record = RecordTextBox.Text
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Trim();

        if (string.IsNullOrWhiteSpace(record))
        {
            MessageBox.Show(
                "Enter an ASTM record before adding it.",
                "Missing record",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        if (record.Any(character => character > 0x7F))
        {
            MessageBox.Show(
                "ASTM records must contain ASCII characters only.",
                "Invalid record",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        RecordsListBox.Items.Add(record);
        ApplyRecordTemplate();
    }

    private void RemoveRecord_Click(object sender, RoutedEventArgs e)
    {
        if (RecordsListBox.SelectedItem is not null)
            RecordsListBox.Items.Remove(RecordsListBox.SelectedItem);
    }

    private void ClearRecords_Click(object sender, RoutedEventArgs e)
    {
        RecordsListBox.Items.Clear();
    }

    private void UseMessage_Click(object sender, RoutedEventArgs e)
    {
        if (RecordsListBox.Items.Count == 0)
        {
            MessageBox.Show(
                "Add at least one ASTM record.",
                "No records",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        GeneratedMessage = string.Join(
            Environment.NewLine,
            RecordsListBox.Items.Cast<string>());

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}