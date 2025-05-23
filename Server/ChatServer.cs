﻿using System.Collections.Concurrent;
using Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;
using Npgsql;
using System.Threading.Tasks;
using System.Text;


namespace Server;

/// This is a very basic implementation of a chat server.
/// There are lot of things to improve...
public class ChatServer
{
    /// The message history
    private readonly ConcurrentQueue<ChatMessage> messageQueue = new();

    /// All the chat clients
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ChatMessage>> waitingClients = new();

    // Sammlung der aktuell verbundenen Benutzer (im Speicher)
    private readonly HashSet<string> activeUsers = new();
    /// The lock object for concurrency
    private readonly object lockObject = new();

    // Verlauf
    private readonly string connectionString = "Host=localhost;Port=5432;Username=postgres;Password=0000;Database=postgres";

    private readonly ConcurrentDictionary<string, ConsoleColor> usernameColors = new();

    private readonly ConcurrentDictionary<string, (string LastMessage, DateTime LastMessageTimestamp)> userMessageHistory = new();

    const int MESSAGE_COOLDOWN_MILLISECONDS = 100;




    /// Configures the web services.
    /// <param name="app">The application.</param>
    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();

        app.UseEndpoints(endpoints =>
        {
            // Endpunkt zur Benutzerregistrierung
            endpoints.MapPost("/messages/id", async context =>
            {
                var message = await context.Request.ReadFromJsonAsync<ChatMessage>();

                if (string.IsNullOrEmpty(message?.Sender))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    Console.WriteLine("Name ist erforderlich.");
                    return;
                }

                lock (lockObject)
                {
                    // Überprüft, ob der Benutzername bereits aktiv ist
                    if (activeUsers.Contains(message.Sender))
                    {
                        context.Response.StatusCode = StatusCodes.Status409Conflict;
                        Console.WriteLine($"Benutzername '{message.Sender}' ist bereits verbunden.");
                        context.Response.WriteAsync("Name ist bereits vergeben.");
                        return;
                    }
                    else
                    {
                        // Fügt den Benutzer zu den aktiven Benutzern hinzu
                        activeUsers.Add(message.Sender);
                    }
                }

                if (await CheckIfUserExists(message.Sender))
                {
                    // Benutzer existiert bereits in der Datenbank, Farbe abrufen
                    var (_, assignedColor) = await GetUserByUsername(message.Sender);
                    var responseObj = new { AssignedColor = assignedColor.ToString() };
                    Console.WriteLine($"Client '{message.Sender}' erneut verbunden mit Farbe '{assignedColor}'");
                    await context.Response.WriteAsJsonAsync(responseObj);
                }
                else
                {
                    // Generiert eine eindeutige Farbe für den neuen Benutzer
                    var assignedColor = await GenerateUniqueColor();
                    await SaveUserToDatabase(message.Sender, assignedColor);

                    var responseObj = new { AssignedColor = assignedColor.ToString() };
                    Console.WriteLine($"Client '{message.Sender}' zur Datenbank hinzugefügt mit Farbe '{assignedColor}'");
                    await context.Response.WriteAsJsonAsync(responseObj);
                }
            });

            // Endpunkt zum Abmelden eines Benutzers
            endpoints.MapDelete("/users/{username}", context =>
            {
                var username = context.Request.RouteValues["username"]?.ToString();

                // Entfernt den Benutzer aus der aktiven Benutzerliste
                if (string.IsNullOrEmpty(username))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return context.Response.WriteAsync("Benutzername ist erforderlich.");
                }

                lock (lockObject)
                {
                    if (activeUsers.Remove(username))
                    {
                        Console.WriteLine($"Benutzer '{username}' hat den Chat verlassen.");
                        return context.Response.WriteAsync("Benutzer erfolgreich abgemeldet");
                    }
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return context.Response.WriteAsync("Benutzer nicht gefunden");
            });

            // The endpoint to register a client to the server to subsequently receive the next message
            // This endpoint utilizes the Long-Running-Requests pattern.
            endpoints.MapGet("/messages", async context =>
            {
                var tcs = new TaskCompletionSource<ChatMessage>();

                context.Request.Query.TryGetValue("id", out var rawId);
                var id = rawId.ToString();

                Console.WriteLine($"Client '{id}' registriert");

                // register a client to receive the next message
                var error = true;
                lock (this.lockObject)
                {
                    if (this.waitingClients.ContainsKey(id))
                    {
                        if (this.waitingClients.TryRemove(id, out _))
                        {
                            Console.WriteLine($"Client '{id}' von wartenden Clients entfernt");
                        }
                    }

                    if (this.waitingClients.TryAdd(id, tcs))
                    {
                        Console.WriteLine($"Client '{id}' zu wartenden Clients hinzugefügt");
                        error = false;
                    }
                }

                // if anything went wrong send out an error message
                if (error)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync("Interner Serverfehler.");
                }

                // otherwise wait for the next message broadcast
                var message = await tcs.Task;

                Console.WriteLine($"Client '{id}' erhielt Nachricht: {message.Content}");

                // send out the next message
                await context.Response.WriteAsJsonAsync(message);
            });
            // Endpunkts für die Statistik
            endpoints.MapGet("/statistics", async context =>
            {
                try
                {
                    var stats = await GetChatStatistics();
                    await context.Response.WriteAsJsonAsync(stats);
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    Console.WriteLine($"Fehler beim Berechnen der Statistik: {ex.Message}");
                    await context.Response.WriteAsync("Fehler beim Berechnen der Statistik.");
                }
            });

            // This endpoint is for sending messages into the chat
            endpoints.MapPost("/messages", async context =>
            {
                var message = await context.Request.ReadFromJsonAsync<ChatMessage>();

                if (message == null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Nachricht ungültig.");
                    return;
                }

                Console.WriteLine($"Nachricht vom Client empfangen: {message.Content}");

                // Spam-Verhinderung: Cooldown prüfen
                if (userMessageHistory.TryGetValue(message.Sender, out var userHistory))
                {
                    // Prüfen, ob der Benutzer innerhalb des Cooldowns (in Millisekunden) eine Nachricht gesendet hat
                    if ((DateTime.UtcNow - userHistory.LastMessageTimestamp).TotalMilliseconds < MESSAGE_COOLDOWN_MILLISECONDS)
                    {
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        await context.Response.WriteAsync($"Bitte warten Sie noch {MESSAGE_COOLDOWN_MILLISECONDS / 1000} Sekunde(n), bevor Sie eine weitere Nachricht senden.");
                        return;
                    }

                    // Prüfen, ob die aktuelle Nachricht innerhalb der gleichen Sekunde wie die letzte gesendet wurde
                    if ((DateTime.UtcNow - userHistory.LastMessageTimestamp).TotalMilliseconds < 1000)
                    {
                        string currentContent = message.Content.Trim().ToLower();

                        string lastContent = userHistory.LastMessage.Trim().ToLower();

                        // Überprüfen, ob die aktuelle Nachricht identisch zur letzten ist
                        if (currentContent == lastContent)
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsync("Das wiederholte Senden derselben Nachricht in derselben Sekunde ist nicht erlaubt.");
                            return;
                        }
                    }
                }
                // Nachricht zensieren
                var (censoredMessage, wasCensored) = await CensorMessage(message.Content);
                message.Content = censoredMessage;

                // Nachricht wurde erfolgreich verarbeitet, Benutzerhistorie aktualisieren
                userMessageHistory[message.Sender] = (message.Content, DateTime.UtcNow);

                try
                {
                    // Speichere die Nachricht in der Datenbank
                    var (senderId, senderColor) = await GetUserByUsername(message.Sender);
                    message.SenderColor = senderColor;

                    await SaveMessageToDatabase(message);

                    // Nachricht an alle wartenden Clients senden
                    lock (this.lockObject)
                    {
                        foreach (var (id, client) in this.waitingClients)
                        {
                            client.TrySetResult(message);
                        }
                    }

                    Console.WriteLine($"Nachricht an alle Clients gesendet: {message.Content}");

                    if (wasCensored)
                    {
                        // Rückmeldung an den Benutzer (HTTP-Antwort)
                        context.Response.StatusCode = StatusCodes.Status201Created;
                        await context.Response.WriteAsync($"Diese Nachricht wird gefiltert: {message.Content}");
                    }

                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    Console.WriteLine($"Fehler beim Speichern der Nachricht in der Datenbank: {ex.Message}");
                    await context.Response.WriteAsync("Fehler beim Speichern der Nachricht.");
                    return;
                }
            });

            // Chat History Endpoint
            endpoints.MapGet("/chat/history", async context =>
            {
                string? username = context.Request.Query["username"];

                var messages = await GetChatHistoryFromDatabase(username);

                if (messages == null || !messages.Any())
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync("Keine Nachrichten gefunden.");
                    return;
                }

                await context.Response.WriteAsJsonAsync(messages);
            });

            // Delete Chat History Endpoint
            endpoints.MapDelete("/chat/history", async context =>
            {
                var username = context.Request.Query["username"].ToString();

                if (string.IsNullOrEmpty(username))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Benutzername erforderlich.");
                    return;
                }

                bool result = await DeleteChatHistory(username);
                if (result)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsync($"Chat-Verlauf für Benutzer '{username}' erfolgreich gelöscht.");
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync($"Kein Chat-Verlauf für Benutzer '{username}' gefunden.");
                }
            });

            // Get Chat History for Last X Hours Endpoint
            endpoints.MapGet("/chat/hours", async context =>
            {
                string? hoursStr = context.Request.Query["hours"];
                if (string.IsNullOrEmpty(hoursStr) || !int.TryParse(hoursStr, out var hours) || hours <= 0)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Ungültiger oder fehlender 'hours'-Parameter.");
                    return;
                }

                // Berechne das Datum basierend auf der Anzahl der Stunden
                var Date = DateTime.UtcNow.AddHours(-hours);

                try
                {
                    // Nachrichten aus der Datenbank abrufen
                    var messages = await GetChatHistoryFromDatabase(Date);

                    if (messages == null || !messages.Any())
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        await context.Response.WriteAsync($"Keine Nachrichten in den letzten {hours} Stunden gefunden.");
                        return;
                    }

                    // Nachrichten als JSON zurücksenden
                    await context.Response.WriteAsJsonAsync(messages);
                }
                catch (Exception ex)
                {
                    // Fehlerbehandlung
                    Console.WriteLine($"Fehler beim Abrufen des Verlaufs: {ex.Message}");
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync("Ein Fehler ist aufgetreten.");
                }
            });



        });
    }

    // Methode des Chat-Verlaufs
    private async Task SaveMessageToDatabase(ChatMessage message)
    {
        try
        {
            var (senderId, _) = await GetUserByUsername(message.Sender);

            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            string query = "INSERT INTO Chat (Content, timestamp, sender_id) VALUES (@content, @timestamp, @sender_id)";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("content", message.Content);
            command.Parameters.AddWithValue("timestamp", message.Timestamp);
            command.Parameters.AddWithValue("sender_id", senderId);

            await command.ExecuteNonQueryAsync();
            Console.WriteLine($"Nachricht von '{message.Sender}' erfolgreich gespeichert.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Speichern der Nachricht: {ex.Message}");
        }
    }

    // Methode zum Abrufen der Benutzer-ID und Farbe anhand des Benutzernamens
    private async Task<(int UserId, ConsoleColor SenderColor)> GetUserByUsername(string username)
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        string query = "SELECT Id, sender_color FROM Benutzer WHERE Sender = @sender";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("sender", username);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            int userId = reader.GetInt32(0);
            if (Enum.TryParse(reader.GetString(1), out ConsoleColor senderColor))
                return (userId, senderColor);
        }

        throw new Exception("User not found");
    }

    // Methode zum Wörter Filter
    private async Task<List<string>> LoadFilterWordsFromDatabase()
    {
        const string query = "SELECT word FROM badwords";
        var filterWords = new List<string>();

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(query, connection);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var word = reader.GetString(0).Trim().ToLower();
                Console.WriteLine($"Geladenes Filterwort: {word}"); // Debugging-Ausgabe
                filterWords.Add(word);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Laden der Filterwörter: {ex.Message}");
        }

        return filterWords;
    }
    private async Task<(string CensoredMessage, bool WasCensored)> CensorMessage(string content)
    {
        bool wasCensored = false;
        var filterWords = await LoadFilterWordsFromDatabase();

        foreach (var word in filterWords)
        {
            // Regex für exakte Wortübereinstimmung (unabhängig von Groß-/Kleinschreibung)
            string pattern = $@"\b{Regex.Escape(word)}\b";

            if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
            {
                wasCensored = true;

                // Ersetzt das gefundene Wort durch Sternchen
                string replacement = new string('*', word.Length);
                content = Regex.Replace(content, pattern, replacement, RegexOptions.IgnoreCase);
            }
        }

        Console.WriteLine($"Zensierte Nachricht: {content}, Wurde zensiert: {wasCensored}");
        return (content, wasCensored);
    }

    private async Task<List<ChatMessage>> GetChatHistoryFromDatabase(DateTime? Date = null, string? username = null)
    {
        var messages = new List<ChatMessage>();

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Dynamische SQL-Abfrage erstellen
            var query = new StringBuilder(
                @"SELECT Chat.timestamp, Benutzer.Sender, Chat.Content, Benutzer.sender_color
              FROM Chat
              JOIN Benutzer ON Chat.sender_id = Benutzer.Id
              WHERE 1=1"
            );

            var parameters = new List<NpgsqlParameter>();

            // Filter für Datum hinzufügen
            if (Date.HasValue)
            {
                query.Append(" AND Chat.timestamp >= @Date");
                parameters.Add(new NpgsqlParameter("Date", Date.Value));
            }

            // Filter für Benutzernamen hinzufügen
            if (!string.IsNullOrEmpty(username))
            {
                query.Append(" AND Benutzer.Sender = @username");
                parameters.Add(new NpgsqlParameter("username", username));
            }

            // Sortierung nach Zeitstempel
            query.Append(" ORDER BY Chat.timestamp");

            using var command = new NpgsqlCommand(query.ToString(), connection);
            command.Parameters.AddRange(parameters.ToArray());

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messages.Add(new ChatMessage
                {
                    Timestamp = reader.GetDateTime(0),
                    Sender = reader.GetString(1),
                    Content = reader.GetString(2),
                    SenderColor = Enum.Parse<ConsoleColor>(reader.GetString(3))
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Abrufen des Chatverlaufs: {ex.Message}");
        }

        return messages;
    }

    private async Task<List<ChatMessage>> GetChatHistoryFromDatabase(string? username)
    {
        var messages = new List<ChatMessage>();

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // SQL-Abfrage ohne Limit
            var query = new StringBuilder(
                @"SELECT Chat.timestamp, Benutzer.Sender, Chat.Content, Benutzer.sender_color
              FROM Chat
              JOIN Benutzer ON Chat.sender_id = Benutzer.Id
              WHERE 1=1"
            );

            var parameters = new List<NpgsqlParameter>();

            if (!string.IsNullOrEmpty(username))
            {
                query.Append(" AND Benutzer.Sender = @username");
                parameters.Add(new NpgsqlParameter("username", username));
            }

            query.Append(" ORDER BY Chat.timestamp");

            using var command = new NpgsqlCommand(query.ToString(), connection);
            command.Parameters.AddRange(parameters.ToArray());

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messages.Add(new ChatMessage
                {
                    Timestamp = reader.GetDateTime(0),
                    Sender = reader.GetString(1),
                    Content = reader.GetString(2),
                    SenderColor = Enum.Parse<ConsoleColor>(reader.GetString(3))
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Abrufen des Chatverlaufs: {ex.Message}");
        }

        return messages;
    }
    private async Task<bool> DeleteChatHistory(string username)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            string query = "DELETE FROM Chat WHERE sender_id = (SELECT Id FROM Benutzer WHERE Sender = @username)";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("username", username);

            int rowsAffected = await command.ExecuteNonQueryAsync();
            Console.WriteLine($"Gelöschte Nachrichten für Benutzer '{username}': {rowsAffected}");

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Löschen des Chatverlaufs für Benutzer '{username}': {ex.Message}");
            return false;
        }
    }



    // Methode zur Überprüfung, ob ein Benutzer in der Datenbank existiert
    private async Task<bool> CheckIfUserExists(string username)
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        string query = "SELECT COUNT(*) FROM Benutzer WHERE Sender = @sender";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("sender", username);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    // Methode zur Generierung einer eindeutigen Farbe für neue Benutzer
    private async Task<ConsoleColor> GenerateUniqueColor()
    {
        var allColors = Enum.GetValues(typeof(ConsoleColor)).Cast<ConsoleColor>().ToList();
        var excludedColors = new List<ConsoleColor> { ConsoleColor.Black, ConsoleColor.White, ConsoleColor.Gray };
        allColors = allColors.Except(excludedColors).ToList();

        List<ConsoleColor> usedColors = new List<ConsoleColor>();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Ruft alle bereits verwendeten Farben aus der Datenbank ab
        string query = "SELECT sender_color FROM Benutzer";
        using var command = new NpgsqlCommand(query, connection);
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            if (Enum.TryParse(reader.GetString(0), out ConsoleColor color))
                usedColors.Add(color);
        }

        // Bestimmt verfügbare Farben, die noch nicht vergeben sind
        var availableColors = allColors.Except(usedColors).ToList();
        if (!availableColors.Any())
            availableColors = allColors;

        // Wählt zufällig eine verfügbare Farbe aus
        return availableColors[new Random().Next(availableColors.Count)];
    }

    // Methode zum Speichern eines neuen Benutzers in der Datenbank
    private async Task SaveUserToDatabase(string username, ConsoleColor color)
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        string query = "INSERT INTO Benutzer (Sender, sender_color) VALUES (@sender, @sender_color)";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("sender", username);
        command.Parameters.AddWithValue("sender_color", color.ToString());

        await command.ExecuteNonQueryAsync();
    }

    // Methode zur Berechnung der Statistik 
    private async Task<Dictionary<string, object>> GetChatStatistics()
    {
        var stats = new Dictionary<string, object>();

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Gesamtanzahl der Nachrichten 
            var totalMessagesQuery = @"
            SELECT COUNT(*)
            FROM Chat
            WHERE Content NOT IN ('Hallo, ich habe mich dem Chat angeschlossen!', 'Ich habe den Chat verlassen!')";
            using var totalCommand = new NpgsqlCommand(totalMessagesQuery, connection);
            var totalMessages = Convert.ToInt32(await totalCommand.ExecuteScalarAsync());
            stats["totalMessages"] = totalMessages;

            // Durchschnittliche Nachrichtenanzahl pro Benutzer 
            var averageMessagesQuery = @"
            SELECT AVG(message_count)
            FROM (
                SELECT COUNT(*) AS message_count
                FROM Chat
                WHERE Content NOT IN ('Hallo, ich habe mich dem Chat angeschlossen!', 'Ich habe den Chat verlassen!')
                GROUP BY sender_id
            ) AS counts";
            using var avgCommand = new NpgsqlCommand(averageMessagesQuery, connection);
            var averageMessages = Convert.ToDouble(await avgCommand.ExecuteScalarAsync());
            stats["averageMessagesPerUser"] = Math.Round(averageMessages, 2);

            // Top 3 Benutzer mit den meisten Nachrichten 
            var topUsersQuery = @"
            SELECT Benutzer.Sender, COUNT(*) AS message_count
            FROM Chat
            JOIN Benutzer ON Chat.sender_id = Benutzer.Id
            WHERE Content NOT IN ('Hallo, ich habe mich dem Chat angeschlossen!', 'Ich habe den Chat verlassen!')
            GROUP BY Benutzer.Sender
            ORDER BY message_count DESC
            LIMIT 3";
            using var topUsersCommand = new NpgsqlCommand(topUsersQuery, connection);
            using var reader = await topUsersCommand.ExecuteReaderAsync();

            var topUsers = new Dictionary<string, int>();
            while (await reader.ReadAsync())
            {
                topUsers[reader.GetString(0)] = reader.GetInt32(1);
            }
            stats["topUsers"] = topUsers;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Abrufen der Statistikdaten: {ex.Message}");
            throw;
        }

        return stats;
    }

}
