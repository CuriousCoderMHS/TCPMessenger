using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using Microsoft.Win32;


namespace TCPMessenger;

public partial class MainWindow : Window
{
    private const byte Enq = 0x05, Ack = 0x06, Nak = 0x15, Eot = 0x04;
    private const byte Stx = 0x02, Etx = 0x03, Etb = 0x17, Cr = 0x0D, Lf = 0x0A;

    private enum MessageFormat { Hl7Mllp, AstmE1381 }

    private readonly object _logLock = new();
    private readonly string _communicationLogPath;

    private sealed class AstmSession    
    {
        private readonly object _lock = new();
        private TaskCompletionSource<byte>? _reply;
        public int ExpectedFrameNumber { get; set; } = 1;



        public Task<byte> WaitForReply()
        {
            lock (_lock)
            {
                _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _reply.Task;
            }
        }

        public void ReceiveReply(byte reply)
        {
            lock (_lock)
            {
                _reply?.TrySetResult(reply);
                _reply = null;
            }
        }

        public void ResetIncomingMessage() => ExpectedFrameNumber = 1;
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _hostCancellation;
    private readonly List<TcpClient> _clients = new();
    private readonly object _clientsLock = new();
    private readonly Dictionary<TcpClient, AstmSession> _astmSessions = new();
    private readonly object _astmSessionsLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _astmSendLock = new(1, 1);

    private MessageFormat _format = MessageFormat.AstmE1381;
    private bool _runningAsHost;

    public MainWindow()
    {
        InitializeComponent();
        _communicationLogPath = CreateCommunicationLogFile();
        UpdateModeUi();
        AddMessage($"Communication log: {_communicationLogPath}");
    }

    private static string CreateCommunicationLogFile()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCPMessenger",
            "Logs");

        Directory.CreateDirectory(directory);

        string fileName = $"communication-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "TCPMessenger communication log" + Environment.NewLine);
        return path;
    }

    private void AddMessage(string text)
    {
        string entry =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}";

        // Write the same entry that is shown in the on-screen communication log.
        lock (_logLock)
            File.AppendAllText(_communicationLogPath, entry);

        _ = Dispatcher.InvokeAsync(() =>
        {
            MessagesTextBox.AppendText(entry);
            MessagesTextBox.ScrollToEnd();
        });
    }

    private void ExportLogButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export communication log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"TCPMessenger-log-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            lock (_logLock)
                File.Copy(_communicationLogPath, dialog.FileName, overwrite: true);

            AddMessage($"Communication log exported to: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AddMessage($"Log export failed: {ex.Message}");
        }
    }

    private bool IsHostMode => ModeComboBox.SelectedIndex == 0;

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            UpdateModeUi();
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _format = FormatComboBox.SelectedIndex == 1
            ? MessageFormat.AstmE1381
            : MessageFormat.Hl7Mllp;
        
        if (IsLoaded)
            UpdateBuildMessageButton();

    }

    private void UpdateBuildMessageButton()
    {
        if (_format == MessageFormat.AstmE1381)
        {
            BuildMessageButton.Content = "Build ASTM";
        }
        else
        {
            BuildMessageButton.Content = "Build HL7";
        }     
    }

    private void UpdateModeUi()
    {
        bool host = IsHostMode;

        HostSettingsPanel.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        ClientSettingsPanel.Visibility = host ? Visibility.Collapsed : Visibility.Visible;

        UpdateConnectionButton();
    }

    private bool IsConnectionActive() =>
        _listener is not null || GetConnectedClients().Length > 0;

    private void UpdateConnectionButton()
    {
        bool active = IsConnectionActive();

        ConnectionButton.Content = active
            ? "Disconnect"
            : IsHostMode ? "Start host" : "Connect";

        ConnectionButton.Background = active
            ? Brushes.IndianRed
            : (Brush)FindResource("AccentBrush");
    }

    private async void ConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsConnectionActive())
        {
            DisconnectButton_Click(sender, e);
            return;
        }

        if (IsHostMode)
            await StartHostAsync();
        else
            await ConnectClientAsync();
    }

    private async Task StartHostAsync()
    {
        if (_listener is not null)
        {
            AddMessage("The host is already running.");
            return;
        }

        if (!int.TryParse(ListenPortTextBox.Text, out int port) ||
            port is < 1 or > 65535)
        {
            AddMessage("Enter a valid host port (1–65535).");
            return;
        }

        try
        {
            _runningAsHost = true;
            _hostCancellation = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            UpdateConnectionButton();
            AddMessage($"Host started on TCP port {port}.");

            await AcceptClientsAsync(_hostCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AddMessage("Host stopped.");
        }
        catch (Exception ex)
        {
            AddMessage($"Host error: {ex.Message}");
        }
        finally
        {
            _listener?.Stop();
            _listener = null;

            _hostCancellation?.Dispose();
            _hostCancellation = null;

            _ = Dispatcher.InvokeAsync(UpdateConnectionButton);
        }
    }

    private async Task AcceptClientsAsync(CancellationToken token)
    {
        while (_listener is not null && !token.IsCancellationRequested)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(token);

            AddClient(client);
            AddMessage($"Client connected: {client.Client.RemoteEndPoint}");

            _ = ReceiveMessagesAsync(client);
        }
    }

    private async Task ConnectClientAsync()
    {
        if (!int.TryParse(RemotePortTextBox.Text, out int port) ||
            port is < 1 or > 65535)
        {
            AddMessage("Enter a valid server port (1–65535).");
            return;
        }

        string host = RemoteHostTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            AddMessage("Enter a server IP address or hostname.");
            return;
        }

        try
        {
            _runningAsHost = false;

            var client = new TcpClient();
            await client.ConnectAsync(host, port);

            AddClient(client);
            UpdateConnectionButton();

            AddMessage($"Connected to {client.Client.RemoteEndPoint}");

            _ = ReceiveMessagesAsync(client);
        }
        catch (Exception ex)
        {
            AddMessage($"Connection failed: {ex.Message}");
            UpdateConnectionButton();
        }
    }

    private async Task ReceiveMessagesAsync(TcpClient client)
    {
        try
        {
            if (_format == MessageFormat.AstmE1381)
                await ReceiveAstmAsync(client);
            else
                await ReceiveHl7Async(client);
        }
        catch (Exception ex)
        {
            AddMessage($"Connection closed: {ex.Message}");
        }
        finally
        {
            RemoveClient(client);
            client.Close();

            _ = Dispatcher.InvokeAsync(UpdateConnectionButton);
        }
    }

    private async Task ReceiveHl7Async(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] readBuffer = new byte[4096];
        var pending = new List<byte>();

        while (true)
        {
            int received = await stream.ReadAsync(readBuffer);

            if (received == 0)
                return;

            LogReceivedBytes(readBuffer, received);
            pending.AddRange(readBuffer.Take(received));

            while (TryTakeHl7Frame(pending, out byte[] frame))
            {
                string message = Encoding.UTF8.GetString(
                    frame,
                    1,
                    frame.Length - 3);

                message = NormalizeHl7Message(message);

                AddMessage(
                    $"HL7 received:{Environment.NewLine}" +
                    message.Replace("\r", Environment.NewLine));

                // Do not acknowledge an ACK, preventing ACK loops.
                if (IsHl7Acknowledgement(message))
                {
                    AddMessage("HL7 ACK received.");
                    continue;
                }

                string acknowledgement;

                try
                {
                    acknowledgement = BuildHl7Acknowledgement(
                        message,
                        "AA",
                        "Message accepted");
                }
                catch (Exception ex)
                {
                    AddMessage($"Cannot create HL7 ACK: {ex.Message}");
                    continue;
                }

                await WriteRawAsync(
                    client,
                    CreateHl7Frame(acknowledgement));

                AddMessage(
                    $"HL7 MSA acknowledgement sent:{Environment.NewLine}" +
                    acknowledgement.Replace("\r", Environment.NewLine));

                // Optional host relay.
                if (_runningAsHost)
                    await BroadcastRawAsync(frame, client);
            }
        }
    }

    private static string BuildHl7Acknowledgement(
    string receivedMessage,
    string acknowledgementCode,
    string acknowledgementText)
    {
        string normalized = NormalizeHl7Message(receivedMessage);

        string? mshSegment = normalized
            .Split('\r', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment =>
                segment.StartsWith("MSH", StringComparison.Ordinal));

        if (mshSegment is null || mshSegment.Length < 4)
            throw new InvalidOperationException(
                "The received message has no valid MSH segment.");

        char fieldSeparator = mshSegment[3];
        string[] fields = mshSegment.Split(fieldSeparator);

        string GetField(int index, string fallback = "") =>
            fields.Length > index && !string.IsNullOrWhiteSpace(fields[index])
                ? fields[index]
                : fallback;

        string encodingCharacters = GetField(1, "^~\\&");
        char componentSeparator = encodingCharacters[0];

        string sendingApplication = GetField(2);
        string sendingFacility = GetField(3);
        string receivingApplication = GetField(4, "TCPMessenger");
        string receivingFacility = GetField(5, "LOCAL");
        string originalControlId = GetField(9, "UNKNOWN");
        string processingId = GetField(10, "P");
        string version = GetField(11, "2.4");

        string messageType = GetField(8);
        string[] messageComponents = messageType.Split(componentSeparator);

        string triggerEvent = messageComponents.Length > 1
            ? messageComponents[1]
            : string.Empty;

        string ackMessageType = string.IsNullOrWhiteSpace(triggerEvent)
            ? "ACK"
            : $"ACK{componentSeparator}{triggerEvent}{componentSeparator}ACK";

        string acknowledgementControlId =
            $"ACK{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        var msh = string.Join(
            fieldSeparator,
            "MSH",
            encodingCharacters,
            receivingApplication,
            receivingFacility,
            sendingApplication,
            sendingFacility,
            timestamp,
            "",
            ackMessageType,
            acknowledgementControlId,
            processingId,
            version);

        var msa = string.Join(
            fieldSeparator,
            "MSA",
            acknowledgementCode,
            originalControlId,
            acknowledgementText);

        return $"{msh}\r{msa}\r";
    }

    private static bool IsHl7Acknowledgement(string message)
    {
        string normalized = NormalizeHl7Message(message);

        string? mshSegment = normalized
            .Split('\r', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment =>
                segment.StartsWith("MSH", StringComparison.Ordinal));

        if (mshSegment is null || mshSegment.Length < 4)
            return false;

        char fieldSeparator = mshSegment[3];
        string[] fields = mshSegment.Split(fieldSeparator);

        if (fields.Length <= 8)
            return false;

        string messageType = fields[8];
        char componentSeparator =
            fields.Length > 1 && fields[1].Length > 0
                ? fields[1][0]
                : '^';

        string messageCode = messageType
            .Split(componentSeparator)
            .FirstOrDefault() ?? string.Empty;

        return messageCode.Equals(
            "ACK",
            StringComparison.OrdinalIgnoreCase);
    }
    private async Task ReceiveAstmAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] readBuffer = new byte[4096];
        var pending = new List<byte>();
        AstmSession session = GetAstmSession(client);

        while (true)
        {
            int received = await stream.ReadAsync(readBuffer);

            if (received == 0)
                return;

            LogReceivedBytes(readBuffer, received);
            pending.AddRange(readBuffer.Take(received));

            while (await ProcessAstmBufferAsync(client, session, pending))
            {
            }
        }
    }

    private async Task<bool> ProcessAstmBufferAsync(
        TcpClient client,
        AstmSession session,
        List<byte> bytes)
    {
        if (bytes.Count == 0)
            return false;

        byte first = bytes[0];

        if (first == Enq)
        {
            bytes.RemoveAt(0);
            session.ResetIncomingMessage();

            await WriteRawAsync(client, new[] { Ack });
            //AddMessage("ASTM: ENQ received; ACK sent.");

            return true;
        }

        if (first == Eot)
        {
            bytes.RemoveAt(0);
            session.ResetIncomingMessage();

            AddMessage("ASTM: EOT received; session complete.");
            return true;
        }

        if (first == Ack || first == Nak)
        {
            bytes.RemoveAt(0);
            session.ReceiveReply(first);

            return true;
        }

        if (first != Stx)
        {
            bytes.RemoveAt(0);
            AddMessage($"ASTM: unexpected byte received: {FormatSocketByte(first)}");

            return true;
        }

        int frameEnd = FindCrLf(bytes);

        if (frameEnd < 0)
            return false;

        byte[] frame = bytes.Take(frameEnd + 2).ToArray();
        bytes.RemoveRange(0, frameEnd + 2);

        if (!TryValidateAstmFrame(
                frame,
                session.ExpectedFrameNumber,
                out string payload,
                out bool isFinal,
                out string error))
        {
            await WriteRawAsync(client, new[] { Nak });
            AddMessage($"ASTM: invalid frame; NAK sent. {error}");

            return true;
        }

        await WriteRawAsync(client, new[] { Ack });

        AddMessage(
            $"ASTM frame {session.ExpectedFrameNumber} valid; ACK sent.{Environment.NewLine}{payload}");

        session.ExpectedFrameNumber = (session.ExpectedFrameNumber + 1) % 8;

        if (isFinal)
        {
            //AddMessage("ASTM: final ETX frame received.");
            session.ResetIncomingMessage();
        }

        return true;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        TcpClient[] targets = GetConnectedClients();

        if (targets.Length == 0)
        {
            AddMessage("No connected peers.");
            return;
        }

        try
        {
            if (_format == MessageFormat.Hl7Mllp)
            {
                string hl7Message = MessageTextBox.Text;

                if (string.IsNullOrWhiteSpace(hl7Message))
                {
                    AddMessage("Enter an HL7 message.");
                    return;
                }

                // HL7 requires CR between segments.
                hl7Message = NormalizeHl7Message(hl7Message);

                byte[] mllpFrame = CreateHl7Frame(hl7Message);

                foreach (TcpClient client in targets)
                    await WriteRawAsync(client, mllpFrame);

                AddMessage(
                    $"HL7 MLLP message sent:{Environment.NewLine}" +
                    hl7Message.Replace("\r", Environment.NewLine));
            }
            else
            {
                List<string> records = MessageTextBox.Text
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Where(record => !string.IsNullOrWhiteSpace(record))
                    .ToList();

                if (records.Count == 0)
                {
                    AddMessage("Enter at least one ASTM record.");
                    return;
                }

                if (!TryValidateAstmMessage(records, out string validationError))
                {
                    AddMessage($"ASTM message validation failed: {validationError}");
                    return;
                }

                foreach (TcpClient client in targets)
                    await SendAstmMessageAsync(client, records);
            }

            MessageTextBox.Clear();
        }
        catch (Exception ex)
        {
            AddMessage($"Send failed: {ex.Message}");
        }
    }

    private static string NormalizeHl7Message(string message)
    {
        return message
            .Replace("\r\n", "\r")
            .Replace('\n', '\r')
            .Trim('\r');
    }
    private async Task SendAstmMessageAsync(TcpClient client, List<string> lines)
    {
        await _astmSendLock.WaitAsync();

        try
        {
            AstmSession session = GetAstmSession(client);

            await SendAndRequireAckAsync(client, session, new[] { Enq }, "ENQ");

            for (int index = 0; index < lines.Count; index++)
            {
                bool isLast = index == lines.Count - 1;
                byte[] frame = CreateAstmFrame(lines[index], index, isLast);

                await SendAndRequireAckAsync(
                    client,
                    session,
                    frame,
                    $"frame {((index + 1) % 8)}");

                //AddMessage($"You: {lines[index]}");
            }

            await WriteRawAsync(client, new[] { Eot });
            AddMessage("ASTM: EOT sent; message complete.");
        }
        finally
        {
            _astmSendLock.Release();
        }
    }

    private async Task SendAndRequireAckAsync(
        TcpClient client,
        AstmSession session,
        byte[] data,
        string description)
    {
        const int retries = 3;

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            Task<byte> replyTask = session.WaitForReply();

            await WriteRawAsync(client, data);

            try
            {
                byte reply = await replyTask.WaitAsync(TimeSpan.FromSeconds(15));

                if (reply == Ack)
                {
                    //AddMessage($"ASTM: ACK received for {description}.");
                    return;
                }

                AddMessage(
                    $"ASTM: NAK received for {description}; retry {attempt}/{retries}.");
            }
            catch (TimeoutException)
            {
                AddMessage(
                    $"ASTM: timeout waiting for ACK to {description}; retry {attempt}/{retries}.");
            }
        }

        throw new InvalidOperationException(
            $"ASTM: communication failed: no ACK for {description}.");
    }

    private async Task BroadcastRawAsync(byte[] data, TcpClient? except = null)
    {
        foreach (TcpClient client in GetConnectedClients(except))
            await WriteRawAsync(client, data);
    }

    private async Task WriteRawAsync(TcpClient client, byte[] data)
    {
        await _writeLock.WaitAsync();

        try
        {
            await client.GetStream().WriteAsync(data);
            LogTransmittedBytes(data);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static byte[] CreateHl7Frame(string message)
    {
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        byte[] frame = new byte[messageBytes.Length + 3];

        frame[0] = 0x0B; // VT: MLLP start block

        Buffer.BlockCopy(
            messageBytes,
            0,
            frame,
            1,
            messageBytes.Length);

        frame[^2] = 0x1C; // FS: MLLP end block
        frame[^1] = 0x0D; // CR: frame terminator

        return frame;
    }

    private static byte[] CreateAstmFrame(  
        string text,
        int frameIndex,
        bool isLastFrame)
    {
        if (text.Any(character => character > 0x7F))
        {
            throw new InvalidOperationException(
                "ASTM E1381 messages must contain ASCII characters only.");
        }

        byte[] payload = Encoding.ASCII.GetBytes(text);
        byte frameNumber = (byte)('0' + ((frameIndex + 1) % 8));
        byte terminator = isLastFrame ? Etx : Etb;

        int checksum = frameNumber + terminator;

        foreach (byte value in payload)
            checksum += value;

        byte[] checksumText = Encoding.ASCII.GetBytes(
            (checksum & 0xFF).ToString("X2"));

        byte[] frame = new byte[payload.Length + 7];

        frame[0] = Stx;
        frame[1] = frameNumber;

        Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);

        int position = payload.Length + 2;

        frame[position++] = terminator;
        frame[position++] = checksumText[0];
        frame[position++] = checksumText[1];
        frame[position++] = Cr;
        frame[position] = Lf;

        return frame;
    }

    private static bool TryValidateAstmFrame(
        byte[] frame,
        int expectedFrameNumber,
        out string payload,
        out bool isFinal,
        out string error)
    {
        payload = string.Empty;
        isFinal = false;
        error = string.Empty;

        if (frame.Length < 7 ||
            frame[0] != Stx ||
            frame[^2] != Cr ||
            frame[^1] != Lf)
        {
            error = "Invalid ASTM frame structure.";
            return false;
        }

        byte expectedNumber = (byte)('0' + expectedFrameNumber);

        if (frame[1] != expectedNumber)
        {
            error = $"Expected frame {expectedFrameNumber}, received {(char)frame[1]}.";
            return false;
        }

        int terminatorPosition = frame.Length - 5;
        byte terminator = frame[terminatorPosition];

        if (terminator != Etb && terminator != Etx)
        {
            error = "Expected ETB or ETX terminator.";
            return false;
        }

        string receivedChecksum = Encoding.ASCII.GetString(frame, frame.Length - 4, 2);

        int checksum = 0;

        for (int index = 1; index <= terminatorPosition; index++)
            checksum += frame[index];

        string expectedChecksum = (checksum & 0xFF).ToString("X2");

        if (!string.Equals(
                receivedChecksum,
                expectedChecksum,
                StringComparison.OrdinalIgnoreCase))
        {
            error = $"Checksum mismatch; expected {expectedChecksum}, received {receivedChecksum}.";
            return false;
        }

        payload = Encoding.ASCII.GetString(frame, 2, terminatorPosition - 2);
        isFinal = terminator == Etx;

        return true;
    }

    private static bool TryValidateAstmMessage(
    IReadOnlyList<string> records,
    out string error)
    {
        error = string.Empty;

        if (records.Count < 2)
        {
            error = "An ASTM message must contain at least a header (H) and terminator (L) record.";
            return false;
        }

        if (!records[0].StartsWith("H|", StringComparison.Ordinal))
        {
            error = "The first ASTM record must be a header record beginning with H|.";
            return false;
        }

        if (!records[^1].StartsWith("L|", StringComparison.Ordinal))
        {
            error = "The last ASTM record must be a terminator record beginning with L|.";
            return false;
        }

        bool hasPatientOrOrder = false;

        for (int index = 0; index < records.Count; index++)
        {
            string record = records[index];

            if (record.Any(character => character > 0x7F))
            {
                error = $"Record {index + 1} contains non-ASCII characters.";
                return false;
            }

            if (record.Length < 2 || record[1] != '|')
            {
                error = $"Record {index + 1} must start with a record type followed by |.";
                return false;
            }

            if (record[0] is not ('H' or 'P' or 'O' or 'R' or 'C' or 'M' or 'Q' or 'L'))
            {
                error = $"Record {index + 1} has unsupported ASTM record type '{record[0]}'.";
                return false;
            }

            if (index > 0 && index < records.Count - 1 && record[0] is 'H' or 'L')
            {
                error = $"Header (H) and terminator (L) records may only appear at the beginning and end.";
                return false;
            }

            if (record[0] is 'P' or 'O')
                hasPatientOrOrder = true;
        }

        if (!hasPatientOrOrder)
        {
            error = "The ASTM message must contain at least a patient (P) or order (O) record.";
            return false;
        }

        return true;
    }

    private static bool TryTakeHl7Frame(List<byte> bytes, out byte[] frame)
    {
        frame = Array.Empty<byte>();

        int start = bytes.IndexOf(0x0B);

        if (start < 0)
        {
            bytes.Clear();
            return false;
        }

        if (start > 0)
            bytes.RemoveRange(0, start);

        for (int index = 1; index < bytes.Count - 1; index++)
        {
            if (bytes[index] == 0x1C && bytes[index + 1] == Cr)
            {
                frame = bytes.Take(index + 2).ToArray();
                bytes.RemoveRange(0, index + 2);
                return true;
            }
        }

        return false;
    }

    private static int FindCrLf(List<byte> bytes)
    {
        for (int index = 0; index < bytes.Count - 1; index++)
        {
            if (bytes[index] == Cr && bytes[index + 1] == Lf)
                return index;
        }

        return -1;
    }

    private void LogReceivedBytes(byte[] buffer, int count)
    {
        string text = string.Concat(
            buffer
                .Take(count)
                .Select(FormatSocketByte));

        AddMessage($"RX: {text}");
    }

    private void LogTransmittedBytes(byte[] buffer)
    {
        string text = string.Concat(buffer.Select(FormatSocketByte));
        AddMessage($"TX: {text}");
    }

    private static string FormatSocketByte(byte value)
    {
        return value switch
        {
            0x02 => "<STX>",
            0x03 => "<ETX>",
            0x04 => "<EOT>",
            0x05 => "<ENQ>",
            0x06 => "<ACK>",
            0x0B => "<VT>",
            0x0D => "<CR>",
            0x0A => "<LF>",
            0x15 => "<NAK>",
            0x17 => "<ETB>",
            0x1C => "<FS>",
            >= 0x20 and <= 0x7E => ((char)value).ToString(),
            _ => $"<0x{value:X2}>"
        };
    }

    private AstmSession GetAstmSession(TcpClient client)
    {
        lock (_astmSessionsLock)
        {
            if (!_astmSessions.TryGetValue(client, out AstmSession? session))
            {
                session = new AstmSession();
                _astmSessions[client] = session;
            }

            return session;
        }
    }

    private TcpClient[] GetConnectedClients(TcpClient? except = null)
    {
        lock (_clientsLock)
        {
            return _clients
                .Where(client => client != except && client.Connected)
                .ToArray();
        }
    }

    private void AddClient(TcpClient client)
    {
        lock (_clientsLock)
            _clients.Add(client);

        _ = GetAstmSession(client);
    }

    private void RemoveClient(TcpClient client)
    {
        lock (_clientsLock)
            _clients.Remove(client);

        lock (_astmSessionsLock)
            _astmSessions.Remove(client);
    }

    private void BuildMessage_Click(object sender, RoutedEventArgs e)
    {
        string[] currentRecords = MessageTextBox.Text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        if(_format == MessageFormat.AstmE1381)
        {
            var builder = new AstmMessageBuilderWindow(currentRecords)
            {
                Owner = this
            };

            if (builder.ShowDialog() != true)
                return;

            FormatComboBox.SelectedIndex = 1;
            _format = MessageFormat.AstmE1381;
            MessageTextBox.Text = builder.GeneratedMessage;
        }
        else if(_format == MessageFormat.Hl7Mllp)
        {
            var builder = new Hl7MessageBuilderWindow(currentRecords)
            {
                Owner = this
            };

            if (builder.ShowDialog() != true)
                return;

            FormatComboBox.SelectedIndex = 2;
            _format = MessageFormat.Hl7Mllp;
            MessageTextBox.Text = builder.GeneratedMessage;
        }   
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _hostCancellation?.Cancel();
        _listener?.Stop();

        lock (_clientsLock)
        {
            foreach (TcpClient client in _clients)
                client.Close();

            _clients.Clear();
        }

        lock (_astmSessionsLock)
            _astmSessions.Clear();

        AddMessage("Disconnected.");
        UpdateConnectionButton();
    }

    

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        DisconnectButton_Click(this, new RoutedEventArgs());
    }
}