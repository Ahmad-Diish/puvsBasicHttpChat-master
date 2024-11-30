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
    private readonly string connectionString = "Host=localhost;Port=5432;Username=postgres;Password=0000;Database=postgres";
    private DateTime lastMessageTimestamp = DateTime.MinValue; // Zeitpunkt der letzten Nachricht
    private string lastMessageContent = string.Empty;         // Inhalt der letzten Nachricht
    private const int MESSAGE_COOLDOWN_SECONDS = 2;           // Cooldown-Zeit in Sekunden
    private readonly HttpClient httpClient;
    private readonly string alias;
    public ConsoleColor userColor { get; private set; }
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private static readonly string COLOR_FILE_PATH = "user_colors.json";

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
            await DisplayStatistics();
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
    //  die Statistik vom Server abzurufen
private async Task GetStatistics()
{
    try
    {
        var response = await httpClient.GetFromJsonAsync<Dictionary<string, object>>("/statistics");
        if (response == null)
        {
            Console.WriteLine("Fehler beim Abrufen der Statistiken: Keine Daten erhalten.");
            return;
        }

        Console.WriteLine("\n--- Chat-Statistik ---");

        // Gesamtanzahl der gesendeten Nachrichten
        if (response.TryGetValue("totalMessages", out var totalMessages) && totalMessages != null)
        {
            Console.WriteLine($"Gesamtanzahl der gesendeten Nachrichten: {totalMessages}");
        }
        else
        {
            Console.WriteLine("Fehler: Gesamtanzahl der Nachrichten konnte nicht abgerufen werden.");
        }

        // Durchschnittliche Nachrichten pro Benutzer
        if (response.TryGetValue("averageMessagesPerUser", out var averageMessagesPerUser) && averageMessagesPerUser != null)
        {
            Console.WriteLine($"Durchschnittliche Nachrichten pro Benutzer: {averageMessagesPerUser}");
        }
        else
        {
            Console.WriteLine("Fehler: Durchschnittliche Nachrichtenanzahl konnte nicht abgerufen werden.");
        }

        // Top 3 Benutzer mit den meisten Nachrichten
        if (response.TryGetValue("topUsers", out var topUsersJson) && topUsersJson != null)
        {
            // Sichere Verarbeitung von `topUsersJson`
            var topUsers = JsonSerializer.Deserialize<Dictionary<string, int>>(topUsersJson.ToString() ?? "{}");
            if (topUsers != null && topUsers.Count > 0)
            {
                Console.WriteLine("Top 3 Benutzer mit den meisten Nachrichten:");
                foreach (var user in topUsers)
                {
                    Console.WriteLine($"- {user.Key}: {user.Value} Nachrichten");
                }
            }
            else
            {
                Console.WriteLine("Keine Benutzer mit Nachrichten gefunden.");
            }
        }
        else
        {
            Console.WriteLine("Fehler: Die Top-Benutzer-Daten sind nicht verfügbar.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Fehler beim Abrufen der Statistiken: {ex.Message}");
    }
}

// Aktualisieren der DisplayStatistics-Methode, um die neue Funktionalität zu verwenden
private async Task DisplayStatistics()
{
    await GetStatistics();
}


    // Methode des Chat-Verlaufs

    public async Task LoadAndDisplayAllMessagesForCurrentUser()
    {
        try
        {
            // URL mit Query-Parameter für den aktuellen Benutzer
            var url = $"/chat/history?username={this.Alias}";

            // Abrufen der Nachrichten über die API
            var messages = await httpClient.GetFromJsonAsync<List<ChatMessage>>(url);

            // Prüfen, ob Nachrichten vorhanden sind
            if (messages != null && messages.Count > 0)
            {
                Console.WriteLine($"Nachrichtenverlauf für Benutzer '{this.Alias}':");

                // Iteration über alle Nachrichten und Ausgabe in der Konsole
                foreach (var message in messages)
                {
                    Console.ForegroundColor = message.SenderColor;
                    Console.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
                }

                Console.ResetColor();
            }
            else
            {
                // Keine Nachrichten gefunden
                Console.WriteLine($"Keine Nachrichten für Benutzer '{this.Alias}' gefunden.");
            }
        }
        catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine($"Keine Nachrichten für Benutzer '{this.Alias}' gefunden.");
        }
    }
    // 2. Alle Nachrichten anzeigen 
    public async Task LoadAndDisplayPreviousMessages()
    {
        try
        {
            // URL für den Abruf aller Nachrichten
            var url = "/chat/history";
            var messages = await httpClient.GetFromJsonAsync<List<ChatMessage>>(url);

            if (messages != null && messages.Count > 0)
            {
                foreach (var message in messages)
                {
                    // Setze die Textfarbe basierend auf SenderColor
                    Console.ForegroundColor = message.SenderColor;

                    // Zeige die Nachricht mit Benutzername, Inhalt und Zeitstempel
                    Console.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");

                    // Setze die Konsolenfarbe zurück
                    Console.ResetColor();
                }
            }
            else
            {
                Console.WriteLine("Keine Nachrichten gefunden.");
            }
        }
        catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine($"Keine Nachrichten  gefunden.");
        }
    }

    // 3. Nachrichten der letzten XX Stunden abrufen und anzeigen
    public async Task LoadAndDisplayChatHistoryLastHours(int hours)
    {
        try
        {
            // Überprüfen, ob die eingegebene Stundenanzahl gültig ist
            if (hours <= 0)
            {
                Console.WriteLine("Die Anzahl der Stunden muss größer als 0 sein.");
                return;
            }

            // Erstelle die URL für die Anfrage mit dem Parameter `hours`
            string url = $"/chat/hours?hours={hours}";

            // Anfrage an den Server senden und Nachrichten abrufen
            var messages = await httpClient.GetFromJsonAsync<List<ChatMessage>>(url);

            // Prüfen, ob Nachrichten empfangen wurden
            if (messages != null && messages.Count > 0)
            {
                Console.WriteLine($"\nChat-Verlauf der letzten {hours} Stunden:");

                // Zeige jede Nachricht mit Zeitstempel, Absender und Inhalt
                foreach (var message in messages)
                {
                    // Textfarbe basierend auf SenderColor setzen
                    Console.ForegroundColor = message.SenderColor;
                    Console.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
                }

                Console.ResetColor(); // Standardfarbe wiederherstellen
            }
            else
            {
                // Nachricht, wenn keine Daten gefunden wurden
                Console.WriteLine($"Keine Nachrichten in den letzten {hours} Stunden gefunden.");
            }
        }
        catch (HttpRequestException httpEx)
        {
            // Fehler im Zusammenhang mit der HTTP-Anfrage behandeln
            Console.WriteLine($"Fehler bei der Serveranfrage: {httpEx.Message}");
        }
        catch (Exception ex)
        {
            // Andere Fehler behandeln
            Console.WriteLine($"Fehler beim Abrufen der Nachrichten: {ex.Message}");
        }
    }


    // 4. Nachrichten löschen
    public async Task DeleteChatHistory()
    {
        string url = $"/chat/history?username={this.Alias}";

        try
        {
            var response = await httpClient.DeleteAsync(url);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Chat-Verlauf für Benutzer '{this.Alias}' erfolgreich gelöscht.");
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.WriteLine($"Kein Chat-Verlauf für Benutzer '{this.Alias}' gefunden.");
            }
            else
            {
                Console.WriteLine($"Fehler beim Löschen des Verlaufs für Benutzer '{this.Alias}': {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Löschen des Verlaufs: {ex.Message}");
        }
    }
}


