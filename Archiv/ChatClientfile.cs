using System.Net.Http.Json;
using Data;
using System.Net;
using System.Text.Json;

namespace Client;

/// <summary>
/// A client for the simple web server
/// </summary>
public class ChatClient
{   
    private DateTime lastMessageTimestamp = DateTime.MinValue; // Zeitpunkt der letzten Nachricht
    private string lastMessageContent = string.Empty;         // Inhalt der letzten Nachricht
    private const int MESSAGE_COOLDOWN_SECONDS = 2;           // Cooldown-Zeit in Sekunden
    private readonly HttpClient httpClient;
    private readonly string alias;
    public ConsoleColor userColor { get; private set; }
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private static readonly string COLOR_FILE_PATH = "user_colors.json";
    private const string GENERAL_LOG_FILE_PATH = "Verlauf/Allgemein.txt";

    public string Alias { get; private set; }
    public ChatClient(string alias, Uri serverUri)
    {
        this.alias = alias;
        this.Alias = alias;
        this.httpClient = new HttpClient();
        this.httpClient.BaseAddress = serverUri;
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

// Wörter Filter
    private List<string> LoadFilterWords()
{
    const string filterFilePath = "woerterfilter.txt";

    // Datei erstellen, falls nicht vorhanden
    if (!File.Exists(filterFilePath))
    {
        File.WriteAllText(filterFilePath, ""); // Leere Datei erstellen
    }

    // Wörter aus der Datei laden
    return File.ReadAllLines(filterFilePath)
               .Where(line => !string.IsNullOrWhiteSpace(line)) // Leere Zeilen ignorieren
               .Select(line => line.Trim().ToLower()) // Trim und Kleinschreibung
               .ToList();
}

private string CensorMessage(string content, out bool wasCensored)
{
    wasCensored = false;
    var filterWords = LoadFilterWords();

    foreach (var word in filterWords)
    {
        if (content.ToLower().Contains(word))
        {
            wasCensored = true;
            var replacement = new string('*', word.Length);
            content = content.Replace(word, replacement, StringComparison.OrdinalIgnoreCase);
        }
    }

    return content;
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
        if (response.IsSuccessStatusCode)
        {
            SaveMessageToGeneralFile(message); 
        }

        return response.IsSuccessStatusCode;
    }

    public async Task<HttpStatusCode> Check()
    {
        var message = new ChatMessage { Sender = this.alias, SenderColor = this.userColor, Content = "Hi hier Methode zur Überprüfung des Namens und der Farbe der Registrierung" };
        var response = await this.httpClient.PostAsJsonAsync($"/messages/id", message);

        return response.StatusCode;
    }

    public async Task<bool> SendMessage(string content)
    {
        // Spam-Verhinderung: Cooldown prüfen
        if ((DateTime.Now - lastMessageTimestamp).TotalSeconds < MESSAGE_COOLDOWN_SECONDS)
        {
            Console.ForegroundColor = ConsoleColor.Green; // Helle grüne Schrift
            Console.WriteLine("Bitte warten Sie einen Moment, bevor Sie eine weitere Nachricht senden.");
            Console.ResetColor();
            return false;
        }

        // Doppelte Nachrichten prüfen
        if (content == lastMessageContent)
        {
            Console.ForegroundColor = ConsoleColor.Yellow; // Gelbe Schrift für Hinweis
            Console.WriteLine("Das wiederholte Senden derselben Nachricht ist nicht zulässig.");
            Console.ResetColor();
            return false;
        }

        // Nachricht zensieren
        bool wasCensored;
        content = CensorMessage(content, out wasCensored);

        if (wasCensored)
        {
            Console.ForegroundColor = ConsoleColor.Red; // Schriftfarbe auf Rot setzen
            Console.WriteLine("Warnung: Ihre Nachricht enthält unzulässige Wörter, die ersetzt wurden.");
            Console.ResetColor(); // Zurücksetzen auf Standardfarbe
        }

        //Durch den Befehl „/statistik“ abrufen
        if (content.ToLower() == "/statistik")
        {
            DisplayStatistics();
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

        if (response.IsSuccessStatusCode)
        {
            lock (Console.Out)
            {
                Console.WriteLine("Nachricht erfolgreich gesendet.");
                Thread.Sleep(100);
            }

            SaveMessageToGeneralFile(message);

            // Letzte Nachricht und Zeitpunkt speichern
            lastMessageContent = content;
            lastMessageTimestamp = DateTime.Now;
        }
        else
        {
            Console.WriteLine("Nachricht konnte nicht gesendet werden.");
        } 

        return response.IsSuccessStatusCode;
    }

    private void DisplayStatistics()
    {
        Console.WriteLine("\n--- Chat-Statistik (Gesamter Chat) ---");

        if (!File.Exists(GENERAL_LOG_FILE_PATH))
        {
            Console.WriteLine("Keine Nachrichten vorhanden.");
            return;
        }

        // Lesen und Analysieren der Nachrichten aus der allgemeinen Datei
        var messageCounts = new Dictionary<string, int>();
        int totalMessages = 0;

        lock (fileLock)
        {
            try
            {
                using (var fileStream = new FileStream(GENERAL_LOG_FILE_PATH, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fileStream))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                 /* Überspringen bestimmter Nachrichten((ohne start - end nachricht))
                if (line.Contains("Hallo, ich habe mich dem Chat angeschlossen!") || 
                    line.Contains("Ich habe den Chat verlassen!"))
                {
                    continue;
                }*/
                        
                        totalMessages++;

                        // Extrahieren des Benutzernamens aus der Nachricht
                        var parts = line.Split(new[] { ": " }, 3, StringSplitOptions.None);
                        if (parts.Length >= 2)
                        {
                            var sender = parts[1].Trim();
                            if (messageCounts.ContainsKey(sender))
                            {
                                messageCounts[sender]++;
                            }
                            else
                            {
                                messageCounts[sender] = 1;
                            }
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Fehler beim Lesen der allgemeinen Datei: {ex.Message}");
                return;
            }
        }
        

        // Ausgabe der Gesamtstatistik
        Console.WriteLine($"Gesamtanzahl der gesendeten Nachrichten: {totalMessages}");

        // Durchschnittliche Anzahl von Nachrichten pro Benutzer
        int userCount = messageCounts.Count;
        double averageMessagesPerUser = userCount > 0 ? (double)totalMessages / userCount : 0;
        Console.WriteLine($"Durchschnittliche Anzahl von Nachrichten pro Benutzer: {averageMessagesPerUser:F2}");

        // Top 3 aktivste Benutzer
        var topUsers = messageCounts
            .OrderByDescending(kvp => kvp.Value)
            .Take(3)
            .ToList();

        Console.WriteLine("Die drei aktivsten Benutzer:");
        foreach (var user in topUsers)
        {
            Console.WriteLine($"- {user.Key}: {user.Value} Nachrichten");
        }

        Console.WriteLine();
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

                if (message != null)
                {
                    if (!IsMessageDuplicate(message))
                    {
                        SaveMessageToFile(message);
                    }

                    await Task.Delay(50);
                    this.OnMessageReceived(message.Sender, message.Content, message.Timestamp, message.SenderColor);
                }
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("Verbindung zum Chat getrennt.");
                break;
            }
        }
    }


    public void CancelListeningForMessages()
    {
        this.cancellationTokenSource.Cancel();
    }

    public async Task Disconnect()
    {
        try
        {
            var leaveMessage = new ChatMessage { Sender = this.alias, SenderColor = this.userColor, Content = $"Ich habe den Chat verlassen!" };
            await this.httpClient.PostAsJsonAsync("/messages", leaveMessage);
            var response = await this.httpClient.DeleteAsync($"/users/{Uri.EscapeDataString(this.alias)}");
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("Erfolgreich vom Server abgemeldet.");
                SaveMessageToGeneralFile(leaveMessage);
            }
            else
            {
                Console.WriteLine("Abmeldung vom Server fehlgeschlagen.");
            }
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

    protected virtual void OnMessageReceived(string sender, string message, DateTime timestamp, ConsoleColor usernamecolor)
    {
        this.MessageReceived?.Invoke(this, new MessageReceivedEventArgs
        {
            Sender = sender,
            Message = message,
            Timestamp = timestamp,
            UsernameColor = usernamecolor
        });
    }

    // Chat-Verlauf Methode

    private readonly object fileLock = new();
    private bool IsMessageDuplicate(ChatMessage message)
    {
        string folderPath = "Verlauf";
        string logFilePath = Path.Combine(folderPath, $"{this.alias}_chat_history.txt");

        if (!File.Exists(logFilePath))
        {
            return false;
        }

        lock (fileLock)
        {
            using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fileStream))
            {
                var lastLine = reader.ReadToEnd().Split(Environment.NewLine).LastOrDefault();
                return lastLine != null && lastLine.Contains($"{message.Timestamp}: {message.Sender}: {message.Content}");
            }
        }
    }

    private void SaveMessageToGeneralFile(ChatMessage message)
    {
        lock (fileLock)
        {
            try
            {
                string folderPath = "Verlauf";
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                using (var fileStream = new FileStream(GENERAL_LOG_FILE_PATH, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(fileStream))
                {
                    writer.WriteLine($"{message.Timestamp}: {message.Sender} : {message.Content}");
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Fehler beim Speichern der Nachricht in der allgemeinen Datei: {ex.Message}");
            }
        }
    }

    private void SaveMessageToFile(ChatMessage message)
    {
        string folderPath = "Verlauf";
        string logFilePath = Path.Combine(folderPath, $"{this.alias}_chat_history.txt");

        lock (fileLock)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                using (var fileStream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(fileStream))
                {
                    writer.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Fehler beim Speichern der Nachricht: {ex.Message}");
            }
        }
    }
    public void DeleteChatHistory()
    {
        string folderPath = "Verlauf";
        string logFilePath = Path.Combine(folderPath, $"{this.alias}_chat_history.txt");

        try
        {
            if (File.Exists(logFilePath))
            {
                File.Delete(logFilePath);
                Console.WriteLine($"Chat-Verlauf für Benutzer '{this.alias}' erfolgreich gelöscht.");
            }
            else
            {
                Console.WriteLine($"Kein Chat-Verlauf für Benutzer '{this.alias}' gefunden.");
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Fehler beim Löschen des Chat-Verlaufs: {ex.Message}");
        }
    }

    public async Task LoadMessagesByDateRange(DateTime startDate, DateTime endDate)
    {
        List<ChatMessage> messages = await Task.Run(() => LoadPreviousMessagesFromFile());

        var filteredMessages = messages
            .Where(m => m.Timestamp.Date >= startDate.Date && m.Timestamp.Date <= endDate.Date)
            .ToList();

        if (filteredMessages.Count > 0)
        {
            Console.WriteLine($"\nChatverlauf vom {startDate.ToShortDateString()} bis {endDate.ToShortDateString()}:");
            foreach (var message in filteredMessages)
            {
                Console.WriteLine($"{message.Timestamp.Date.ToShortDateString()}: {message.Sender}: {message.Content}");
            }
        }
        else
        {
            Console.WriteLine("Keine Nachrichten in diesem Zeitraum gefunden.");
        }
    }

    public void GetChatHistoryLastHours(int hours)
    {
        DateTime cutoffTime = DateTime.Now.AddHours(-hours);

        lock (fileLock)
        {
            if (File.Exists(GENERAL_LOG_FILE_PATH))
            {
                try
                {
                    using (var fileStream = new FileStream(GENERAL_LOG_FILE_PATH, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fileStream))
                    {
                        Console.WriteLine($"\nChat-Verlauf der letzten {hours} Stunden:");
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string[] parts = line.Split(": ", 3, StringSplitOptions.None);
                            if (parts.Length == 3 && DateTime.TryParse(parts[0], out DateTime timestamp))
                            {
                                if (timestamp >= cutoffTime)
                                {
                                    Console.WriteLine(line);
                                }
                            }
                        }
                    }
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"Fehler beim Lesen des Chat-Verlaufs: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("Kein allgemeiner Chat-Verlauf verfügbar.");
            }
        }
    }

    public async Task LoadAndDisplayPreviousMessages()
    {
        List<ChatMessage> messages = await Task.Run(() => LoadPreviousMessagesFromFile());

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

    private List<ChatMessage> LoadPreviousMessagesFromFile()
    {
        List<ChatMessage> messages = new List<ChatMessage>();
        string folderPath = "Verlauf";
        string logFilePath = Path.Combine(folderPath, $"Allgemein.txt");

        try
        {
            if (File.Exists(logFilePath))
            {
                using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fileStream))
                {
                    string[] lines = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string[] parts = line.Split(new[] { ": " }, 3, StringSplitOptions.None);
                        if (parts.Length == 3)
                        {
                            ChatMessage message = new ChatMessage
                            {
                                Timestamp = DateTime.Parse(parts[0]),
                                Sender = parts[1],
                                Content = parts[2],
                                SenderColor = this.userColor
                            };
                            messages.Add(message);
                        }
                    }
                }
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Fehler beim Laden des Chat-Verlaufs: {ex.Message}");
        }

        return messages;
    }

    public async Task SingelLoadAndDisplayPreviousMessages()
    {
        List<ChatMessage> messages = await Task.Run(() => SingelLoadPreviousMessagesFromFile());

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

    private List<ChatMessage> SingelLoadPreviousMessagesFromFile()
    {
        List<ChatMessage> messages = new List<ChatMessage>();
        string folderPath = "Verlauf";
        string logFilePath = Path.Combine(folderPath, $"{this.alias}_chat_history.txt");

        try
        {
            if (File.Exists(logFilePath))
            {
                using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fileStream))
                {
                    string[] lines = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string[] parts = line.Split(new[] { ": " }, 3, StringSplitOptions.None);
                        if (parts.Length == 3)
                        {
                            ChatMessage message = new ChatMessage
                            {
                                Timestamp = DateTime.Parse(parts[0]),
                                Sender = parts[1],
                                Content = parts[2],
                                SenderColor = this.userColor
                            };
                            messages.Add(message);
                        }
                    }
                }
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Fehler beim Laden des Chat-Verlaufs: {ex.Message}");
        }

        return messages;
    }
}
