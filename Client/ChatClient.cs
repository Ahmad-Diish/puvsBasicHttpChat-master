using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Npgsql;
using Data;

namespace Client;

public record RegistrationResponse
{
    public string? AssignedColor { get; init; }
}

/// <summary>
/// A client for the simple web server
/// </summary>
public class ChatClient
{
    private readonly HttpClient httpClient;
    private readonly string alias;
    public ConsoleColor userColor { get; private set; }
    private readonly CancellationTokenSource cancellationTokenSource = new();


    public string Alias { get; private set; }
    public ChatClient(string alias, Uri serverUri)
    {
        this.alias = alias;
        this.Alias = alias;
        this.httpClient = new HttpClient();
        this.httpClient.BaseAddress = serverUri;

    }

    public async Task<bool> Connect()
    {
        var message = new ChatMessage { Sender = this.alias, SenderColor = this.userColor, Content = $"Hallo, ich habe mich dem Chat angeschlossen!" };
        var response = await this.httpClient.PostAsJsonAsync("/messages", message);

        return response.IsSuccessStatusCode;
    }

    public async Task<bool> Check()
    {
        var message = new ChatMessage
        {
            Sender = this.alias,
            Content = string.Empty,
            SenderColor = ConsoleColor.White  // Add default color
        };
        // Sendet eine POST-Anfrage an den Server zur Registrierung des Benutzers
        var response = await this.httpClient.PostAsJsonAsync("/messages/id", message);

        if (response.IsSuccessStatusCode)
        {
            // Liest die Antwort des Servers und deserialisiert sie in RegistrationResponse
            var responseContent = await response.Content.ReadFromJsonAsync<RegistrationResponse>();
            if (responseContent != null && responseContent.AssignedColor != null)
            {
                string colorStr = responseContent.AssignedColor;

                // Versucht, den erhaltenen Farbstring in ConsoleColor zu konvertieren
                if (Enum.TryParse(colorStr, out ConsoleColor assignedColor))
                {
                    // Weist die zugewiesene Farbe dem Benutzer zu
                    this.userColor = assignedColor;
                    return true;
                }
            }
        }
        else if (response.StatusCode == HttpStatusCode.Conflict)
        {

            Console.WriteLine("Dieser Benutzername ist bereits vergeben. Bitte versuchen Sie es mit einem anderen Namen.");
        }
        else if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            Console.WriteLine("Ein Name ist erforderlich.");
        }
        else
        {
            Console.WriteLine("Fehler bei der Registrierung.");
        }

        return false;
    }



    public async Task<bool> SendMessage(string content)
    {
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
            // Rückmeldung vom Server abrufen
            string serverMessage = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(serverMessage))
            {
                Console.ForegroundColor = ConsoleColor.Red; // Hinweis in Cyan
                Console.WriteLine($"Server: {serverMessage}");
                Console.ResetColor();
            }
            else
            {
                lock (Console.Out)
                {
                    Console.WriteLine("Nachricht erfolgreich gesendet.");
                    Thread.Sleep(100);
                }
            }
            return true;
        }
        else
        {
            // Server-Antwort auslesen, um die genaue Fehlermeldung anzuzeigen
            string errorMessage = await response.Content.ReadAsStringAsync();

            if (!string.IsNullOrEmpty(errorMessage))
            {
                if ((int)response.StatusCode == 400) // Bad Request
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Fehler: {errorMessage}");
                    Console.ResetColor();
                }
                else if ((int)response.StatusCode == 429) // Too Many Requests
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Cooldown: {errorMessage}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Serverfehler: {errorMessage}");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.WriteLine("Nachricht konnte nicht gesendet werden (Unbekannter Fehler).");
            }
        }

        return false;
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
                Console.WriteLine("Verbindung zum Chat erstellen.");
                break;
            }
        }
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


