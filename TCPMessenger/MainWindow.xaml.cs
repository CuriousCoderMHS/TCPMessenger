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

namespace TCPMessenger;

public partial class MainWindow : Window
{
    private const byte Enq = 0x05, Ack = 0x06, Nak = 0x15, Eot = 0x04;
    private const byte Stx = 0x02, Etx = 0x03, Etb = 0x17, Cr = 0x0D, Lf = 0x0A;

    private enum MessageFormat { Hl7Mllp, AstmE1381 }

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
        UpdateModeUi();
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
                string message = Encoding.UTF8.GetString(frame, 1, frame.Length - 3);

                AddMessage($"Remote:{Environment.NewLine}{message}");

                if (_runningAsHost)
                    await BroadcastRawAsync(frame, client);
            }
        }
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
            AddMessage("ASTM: ENQ received; ACK sent.");

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
            AddMessage("ASTM: final ETX frame received.");
            session.ResetIncomingMessage();
        }

        return true;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        List<string> lines = MessageTextBox.Text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
        {
            AddMessage("Enter at least one non-empty line.");
            return;
        }

        TcpClient[] targets = GetConnectedClients();

        if (targets.Length == 0)
        {
            AddMessage("No connected peers.");
            return;
        }

        try
        {
            if (_format == MessageFormat.AstmE1381)
            {
                foreach (TcpClient client in targets)
                    await SendAstmMessageAsync(client, lines);
            }
            else
            {
                foreach (string line in lines)
                {
                    await BroadcastRawAsync(CreateHl7Frame(line));
                    AddMessage($"You: {line}");
                }
            }

            MessageTextBox.Clear();
        }
        catch (Exception ex)
        {
            AddMessage($"Send failed: {ex.Message}");
        }
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

                AddMessage($"You: {lines[index]}");
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
                    AddMessage($"ASTM: ACK received for {description}.");
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
            $"ASTM communication failed: no ACK for {description}.");
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
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static byte[] CreateHl7Frame(string text)
    {
        byte[] message = Encoding.UTF8.GetBytes(text);
        byte[] frame = new byte[message.Length + 3];

        frame[0] = 0x0B;
        Buffer.BlockCopy(message, 0, frame, 1, message.Length);
        frame[^2] = 0x1C;
        frame[^1] = Cr;

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

    private void BuildAstmMessage_Click(object sender, RoutedEventArgs e)
    {
        string[] currentRecords = MessageTextBox.Text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

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

    private void AddMessage(string text)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            MessagesTextBox.AppendText(
                $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");

            MessagesTextBox.ScrollToEnd();
        });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        DisconnectButton_Click(this, new RoutedEventArgs());
    }
}