using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace TCPMessenger;

public partial class Hl7MessageBuilderWindow : Window
{
    public string GeneratedMessage { get; private set; } = string.Empty;

    public Hl7MessageBuilderWindow(IEnumerable<string> existingRecords)
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
            "Message Header (MSH)" => "MSH|^~\\&|EHR|HOSPITAL|LIS|LAB|20260811103000||OML^O21^OML_O21|MSG00001|P|2.4",
            "Patient (PID)" => "PID|1||123456^^^HOSPITAL^MR||DOE^JOHN||19800101|M|||123 MAIN ST^^ANYTOWN^NY^12345||5551234567",
            "Patient Visit (PV1)" => "PV1|1|O|123456^^^HOSPITAL^MR|||20260811100000",
            "Common Order (ORC)" => "ORC|NW|ORD12345",
            "Observation Request (OBR)" => "OBR|1|ORD12345||57021-8^Complete blood count^LN|||20260811100000",
            "Specimen Information (SPM)" => "SPM|1|SPEC12345||BLD^Whole Blood",
            "Observation Result (OBX)" => "OBX|1|NM|789-8^Erythrocytes^LN||4.80|10*6/uL|4.50-5.90|N|||F",
            "Custom" => string.Empty,
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
                "Enter an HL7 record before adding it.",
                "Missing record",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        if (record.Any(character => character > 0x7F))
        {
            MessageBox.Show(
                "HL7 records must contain ASCII characters only.",
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
                "Add at least one HL7 record.",
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