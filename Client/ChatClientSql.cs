using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Npgsql;
using Data;

namespace Client;

/// <summary>
/// A client for the simple web server
/// </summary>
public class ChatClient
{
    private readonly HttpClient httpClient;
    private readonly string connectionString = "Host=localhost;Port=5432;Username=postgres;Password=0000;Database=postgres";
    private readonly string alias;
    public ConsoleColor userColor { get; private set; }
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private static readonly string COLOR_FILE_PATH = "user_colors.json";

    public string Alias { get; private set; }
    public ChatClient(string alias, Uri serverUri)
    {
        this.alias = alias;
        this.Alias = alias;
        this.httpClient = new HttpClient { BaseAddress = serverUri };
        this.userColor = LoadOrGenerateColor();
    }

    private ConsoleColor LoadOrGenerateColor()
    {
        var colorMapping = LoadColorMapping();

        if (colorMapping.TryGetValue(alias, out string? savedColor))
        {
            return (ConsoleColor)Enum.Parse(typeof(ConsoleColor), savedColor);
        }

        var newColor = GenerateNewColor();
        colorMapping[alias] = newColor.ToString();
        SaveColorMapping(colorMapping);

        return newColor;
    }

    private ConsoleColor GenerateNewColor()
    {
        var random = new Random();
        List<ConsoleColor> excludedColors = new() { ConsoleColor.White, ConsoleColor.Black, ConsoleColor.Gray };

        var usedColors = LoadColorMapping().Values
            .Select(c => (ConsoleColor)Enum.Parse(typeof(ConsoleColor), c))
            .ToList();

        var availableColors = Enum.GetValues(typeof(ConsoleColor))
            .Cast<ConsoleColor>()
            .Except(excludedColors)
            .Except(usedColors)
            .ToList();

        if (!availableColors.Any())
        {
            availableColors = Enum.GetValues(typeof(ConsoleColor))
                .Cast<ConsoleColor>()
                .Except(excludedColors)
                .ToList();
        }

        return availableColors[random.Next(availableColors.Count)];
    }

    private Dictionary<string, string> LoadColorMapping()
    {
        if (File.Exists(COLOR_FILE_PATH))
        {
            try
            {
                string json = File.ReadAllText(COLOR_FILE_PATH);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }
        return new Dictionary<string, string>();
    }

    private void SaveColorMapping(Dictionary<string, string> colorMapping)
    {
        try
        {
            string json = JsonSerializer.Serialize(colorMapping);
            File.WriteAllText(COLOR_FILE_PATH, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warnung: Farbzuordnung konnte nicht gespeichert werden: {ex.Message}");
        }
    }

    public async Task<bool> Connect()
    {
        var message = new ChatMessage { Sender = this.alias, SenderColor = this.userColor, Content = $"Hallo, ich habe mich dem Chat angeschlossen!" };
        var response = await this.httpClient.PostAsJsonAsync("/messages", message);
        return response.IsSuccessStatusCode;
    }

    public async Task<HttpStatusCode> Check()
    {
        var message = new ChatMessage { Sender = this.alias, SenderColor = this.userColor, Content = "Hi hier Methode zur Überprüfung des Namens und der Farbe der Registrierung" };
        var response = await this.httpClient.PostAsJsonAsync("/messages/id", message);
        return response.StatusCode;
    }

    public async Task<bool> SendMessage(string content)
    {
        if (content.ToLower() == "/statistik")
        {
            await DisplayStatistics();  // Warten Sie auf den Abschluss der Statistikmethode
            return true;
        }

        var message = new ChatMessage
        {
            Sender = this.alias,
            SenderColor = this.userColor,
            Content = content,
            Timestamp = DateTime.Now
        };
        var response = await this.httpClient.PostAsJsonAsync("/messages", message);
        // Nachricht nur dann speichern, wenn der aktuelle Benutzer der Absender ist
        if (response.IsSuccessStatusCode && message.Sender == this.alias)
        {
            await SaveMessage(message);
        }
        return response.IsSuccessStatusCode;
    }

    private async Task DisplayStatistics()
    {
        Console.WriteLine("\n--- Chat-Statistik (Gesamter Chat) ---");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        int totalMessages = 0;
        var messageCounts = new Dictionary<string, int>();

        try
        {
            // Abfrage für die Gesamtzahl der Nachrichten
            string totalQuery = "SELECT COUNT(*) FROM Chat";
            using var totalCommand = new NpgsqlCommand(totalQuery, connection);
            totalMessages = Convert.ToInt32(await totalCommand.ExecuteScalarAsync());

            // Abfrage für die Anzahl der Nachrichten pro Benutzer
            string countQuery = "SELECT Benutzer.Sender, COUNT(*) FROM Chat JOIN Benutzer ON Chat.sender_id = Benutzer.Id GROUP BY Benutzer.Sender";
            using var countCommand = new NpgsqlCommand(countQuery, connection);

            using var reader = await countCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string sender = reader.GetString(0);
                int count = reader.GetInt32(1);
                messageCounts[sender] = count;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Abrufen der Statistikdaten: {ex.Message}");
            return;
        }

        Console.WriteLine($"Gesamtanzahl der gesendeten Nachrichten: {totalMessages}");
        double averageMessagesPerUser = messageCounts.Count > 0 ? (double)totalMessages / messageCounts.Count : 0;
        Console.WriteLine($"Durchschnittliche Anzahl von Nachrichten pro Benutzer: {averageMessagesPerUser:F2}");

        var topUsers = messageCounts.OrderByDescending(kvp => kvp.Value).Take(3).ToList();
        Console.WriteLine("Die drei aktivsten Benutzer:");
        foreach (var user in topUsers)
        {
            Console.WriteLine($"- {user.Key}: {user.Value} Nachrichten");
        }
    }


    public async Task ListenForMessages()
    {
        var cancellationToken = this.cancellationTokenSource.Token;
        while (true)
        {
            try
            {
                var message = await this.httpClient.GetFromJsonAsync<ChatMessage>(
                    $"/messages?id={this.alias}&usercolor={this.userColor}",
                    cancellationToken);
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("Verbindung zum Chat getrennt.");
                break;
            }
        }
    }

    public void CancelListeningForMessages() => this.cancellationTokenSource.Cancel();

    public async Task Disconnect()
    {
        try
        {
            var leaveMessage = new ChatMessage { Sender = this.alias, SenderColor = this.userColor, Content = $"Ich habe den Chat verlassen!" };
            await this.httpClient.PostAsJsonAsync("/messages", leaveMessage);
            await this.httpClient.DeleteAsync($"/users/{Uri.EscapeDataString(this.alias)}");
            Console.WriteLine("Erfolgreich vom Server abgemeldet.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler während der Trennung: {ex.Message}");
        }
        finally
        {
            this.CancelListeningForMessages();
        }
    }

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    protected virtual void OnMessageReceived(string sender, string message, DateTime timestamp, ConsoleColor usernameColor)
    {
        this.MessageReceived?.Invoke(this, new MessageReceivedEventArgs
        {
            Sender = sender,
            Message = message,
            Timestamp = timestamp,
            UsernameColor = usernameColor
        });
    }

    // Verlauf 
    // Stellt sicher, dass der Benutzer in der Tabelle vorhanden ist und gibt die Benutzer-ID zurück
    private async Task<int> EnsureUserExists()
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        string query = "INSERT INTO Benutzer (Sender, sender_color) VALUES (@sender, @sender_color) ON CONFLICT (Sender) DO UPDATE SET sender_color = EXCLUDED.sender_color RETURNING Id";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("sender", alias);
        command.Parameters.AddWithValue("sender_color", userColor.ToString());

        var result = await command.ExecuteScalarAsync();

        if (result is int userId)
        {
            return userId;
        }

        throw new InvalidOperationException("Benutzer-ID konnte nicht abgerufen werden.");
    }


    // Speichert die Nachricht in die Chat-Tabelle
    private async Task SaveMessage(ChatMessage message)
    {
        int senderId = await EnsureUserExists();

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        string query = "INSERT INTO Chat (Content, timestamp, sender_id) VALUES (@content, @timestamp, @sender_id)";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("content", message.Content);
        command.Parameters.AddWithValue("timestamp", message.Timestamp);
        command.Parameters.AddWithValue("sender_id", senderId);

        await command.ExecuteNonQueryAsync();
    }

    // Überprüft, ob die Nachricht ein Duplikat ist
    private async Task<bool> IsMessageDuplicate(ChatMessage message)
    {
        int senderId = await EnsureUserExists();

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        string query = "SELECT COUNT(*) FROM Chat WHERE timestamp = @timestamp AND sender_id = @sender_id AND Content = @content";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("timestamp", message.Timestamp);
        command.Parameters.AddWithValue("sender_id", senderId);
        command.Parameters.AddWithValue("content", message.Content);

        var result = await command.ExecuteScalarAsync();

        // Sicherstellen, dass das Ergebnis nicht null ist und in einen long konvertiert werden kann
        long count = result != null ? (long)result : 0;
        return count > 0;
    }


    // Löscht den Chat-Verlauf des aktuellen Benutzers
    public async Task DeleteChatHistory()
    {
        int senderId = await EnsureUserExists();

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        string query = "DELETE FROM Chat WHERE sender_id = @sender_id";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("sender_id", senderId);

        int rowsAffected = await command.ExecuteNonQueryAsync();
        Console.WriteLine(rowsAffected > 0
            ? $"Chat-Verlauf für Benutzer '{this.alias}' erfolgreich gelöscht."
            : $"Kein Chat-Verlauf für Benutzer '{this.alias}' gefunden.");
    }

    // Lädt Nachrichten innerhalb eines Datumsbereichs
    public async Task LoadMessagesByDateRange(DateTime startDate, DateTime endDate)
    {
        List<ChatMessage> messages = await LoadPreviousMessagesFromDatabase(startDate, endDate);

        if (messages.Count > 0)
        {
            Console.WriteLine($"\nChatverlauf vom {startDate.ToShortDateString()} bis {endDate.ToShortDateString()}:");
            foreach (var message in messages)
            {
                Console.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
            }
        }
        else
        {
            Console.WriteLine("Keine Nachrichten in diesem Zeitraum gefunden.");
        }
    }

    // Lädt Nachrichten der letzten X Stunden
    public async Task GetChatHistoryLastHours(int hours)
    {
        DateTime cutoffTime = DateTime.Now.AddHours(-hours);
        List<ChatMessage> messages = await LoadPreviousMessagesFromDatabase(cutoffTime);

        if (messages.Count > 0)
        {
            Console.WriteLine($"\nChat-Verlauf der letzten {hours} Stunden:");
            foreach (var message in messages)
            {
                Console.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
            }
        }
        else
        {
            Console.WriteLine("Kein Chat-Verlauf verfügbar.");
        }
    }

    // Lädt und zeigt den gesamten Chat-Verlauf an
    public async Task LoadAndDisplayPreviousMessages()
    {
        List<ChatMessage> messages = await LoadPreviousMessagesFromDatabase();

        if (messages.Count > 0)
        {
            Console.WriteLine("\nVorheriger Chat-Verlauf:");
            foreach (var message in messages)
            {
                Console.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
            }
        }
        else
        {
            Console.WriteLine("Kein Chat-Verlauf verfügbar.");
        }
    }

    // Lädt den privaten Chat-Verlauf für den Benutzer
    public async Task SingelLoadAndDisplayPreviousMessages()
    {
        List<ChatMessage> messages = await LoadPreviousMessagesFromDatabase(isPrivateChat: true);

        if (messages.Count > 0)
        {
            Console.WriteLine("\nVorheriger Privat-Chat-Verlauf:");
            foreach (var message in messages)
            {
                Console.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
            }
        }
        else
        {
            Console.WriteLine("Kein Privat-Chat-Verlauf verfügbar.");
        }
    }

    // Hilfsmethode zum Laden der Nachrichten aus der Chat-Tabelle
    private async Task<List<ChatMessage>> LoadPreviousMessagesFromDatabase(DateTime? startDate = null, DateTime? endDate = null, bool isPrivateChat = false)
    {
        var messages = new List<ChatMessage>();

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        string query = "SELECT Chat.timestamp, Benutzer.Sender, Chat.Content, Benutzer.sender_color FROM Chat JOIN Benutzer ON Chat.sender_id = Benutzer.Id WHERE 1=1";

        if (isPrivateChat)
        {
            query += " AND Benutzer.Sender = @sender";
        }
        if (startDate.HasValue && endDate.HasValue)
        {
            query += " AND Chat.timestamp BETWEEN @startDate AND @endDate";
        }
        else if (startDate.HasValue)
        {
            query += " AND Chat.timestamp >= @startDate";
        }

        using var command = new NpgsqlCommand(query, connection);

        if (isPrivateChat)
        {
            command.Parameters.AddWithValue("sender", this.alias);
        }
        if (startDate.HasValue)
        {
            command.Parameters.AddWithValue("startDate", startDate.Value);
        }
        if (endDate.HasValue)
        {
            command.Parameters.AddWithValue("endDate", endDate.Value);
        }

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var message = new ChatMessage
            {
                Timestamp = reader.GetDateTime(0),
                Sender = reader.GetString(1),
                Content = reader.GetString(2),
                SenderColor = reader.IsDBNull(3) ? ConsoleColor.Gray : Enum.Parse<ConsoleColor>(reader.GetString(3))
            };
            messages.Add(message);
        }

        return messages;
    }

}

