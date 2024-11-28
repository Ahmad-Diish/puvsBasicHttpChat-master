using System.Collections.Concurrent;
using Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    /// <summary>
    /// All the chat clients to check if the name or color of user is already taken or not 
    /// </summary>
    private readonly Dictionary<string, ConsoleColor> usernameColors = new();

    /// The lock object for concurrency
    private readonly object lockObject = new();

    // Verlauf
    private readonly string connectionString = "Host=localhost;Port=5432;Username=postgres;Password=0000;Database=postgres";

    /// Configures the web services.
    /// <param name="app">The application.</param>
    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapPost("/messages/id", async context =>
            {
                var message = await context.Request.ReadFromJsonAsync<ChatMessage>();

                if (string.IsNullOrEmpty(message?.Sender))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    Console.WriteLine("Name ist erforderlich.");
                    return;
                }

                if (usernameColors.ContainsKey(message.Sender) || usernameColors.ContainsValue(message.SenderColor))
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    Console.WriteLine("Der 'Name' oder 'Farbe' des Benutzers ist bereits vergeben.");
                    await context.Response.WriteAsync("Name ist bereits vergeben.");
                }
                else
                {
                    usernameColors[message.Sender] = message.SenderColor;
                    Console.WriteLine($"Client '{message.Sender}' zur UsernameColorDict hinzugefügt");
                    await context.Response.WriteAsync("Erfolgreich registriert");
                }
            });

            // Add new endpoint for user disconnection
            endpoints.MapDelete("/users/{username}", context =>
            {
                var username = context.Request.RouteValues["username"]?.ToString();

                if (string.IsNullOrEmpty(username))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return context.Response.WriteAsync("Benutzername ist erforderlich.");
                }

                lock (lockObject)
                {
                    if (usernameColors.Remove(username))
                    {
                        Console.WriteLine($"Benutzer '{username}' aus UsernameColorDict entfernt");
                        return context.Response.WriteAsync("Benutzer erfolgreich abgemeldet");
                    }
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return context.Response.WriteAsync("Benutzer nicht gefunden");
            });
        });


        app.UseEndpoints(endpoints =>
        {
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

                try
                {
                    // Speichere die Nachricht in der Datenbank
                    await SaveMessageToDatabase(message);
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    Console.WriteLine($"Fehler beim Speichern der Nachricht in der Datenbank: {ex.Message}");
                    await context.Response.WriteAsync("Fehler beim Speichern der Nachricht.");
                    return;
                }

                // Sende die Nachricht an wartende Clients
                lock (this.lockObject)
                {
                    foreach (var (id, client) in this.waitingClients)
                    {
                        Console.WriteLine($"Broadcasting an Client '{id}'");
                        client.TrySetResult(message);
                    }
                }

                Console.WriteLine($"Nachricht an alle Clients gesendet: {message.Content}");

                // Bestätige, dass die Nachricht verarbeitet wurde
                context.Response.StatusCode = StatusCodes.Status201Created;
                await context.Response.WriteAsync("Nachricht empfangen und verarbeitet.");
            });


            // Methode des Chat-Verlaufs
            endpoints.MapGet("/chat/history", async context =>
            {
                string? username = context.Request.Query["username"];
                string? limitStr = context.Request.Query["limit"];
                int limit = 0;

                if (!string.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out int parsedLimit))
                {
                    limit = parsedLimit;
                }

                var messages = await GetChatHistoryFromDatabase(username, limit);

                if (messages == null || !messages.Any())
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync("Keine Nachrichten gefunden.");
                    return;
                }

                await context.Response.WriteAsJsonAsync(messages);
            });


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


            endpoints.MapGet("/chat/hours", async context =>
               {
                   string? hoursStr = context.Request.Query["hours"];
                   if (string.IsNullOrEmpty(hoursStr) || !int.TryParse(hoursStr, out var hours) || hours <= 0)
                   {
                       context.Response.StatusCode = StatusCodes.Status400BadRequest;
                       await context.Response.WriteAsync("Ungültiger oder fehlender 'hours'-Parameter.");
                       return;
                   }

                   // Berechne das Startdatum basierend auf der Anzahl der Stunden
                   var startDate = DateTime.UtcNow.AddHours(-hours);

                   try
                   {
                       // Nachrichten aus der Datenbank abrufen
                       var messages = await GetChatHistoryFromDatabase(startDate);

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
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Stelle sicher, dass der Benutzer existiert oder aktualisiere ihn
            int senderId = await EnsureUserExists(message.Sender, message.SenderColor);

            // Speichere die Nachricht in der Chat-Tabelle
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

    private async Task<int> EnsureUserExists(string sender, ConsoleColor senderColor)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Füge den Benutzer ein oder aktualisiere ihn, falls er bereits existiert
            string query = @"
            INSERT INTO Benutzer (Sender, sender_color) 
            VALUES (@sender, @sender_color) 
            ON CONFLICT (Sender) 
            DO UPDATE SET sender_color = EXCLUDED.sender_color 
            RETURNING Id";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("sender", sender);
            command.Parameters.AddWithValue("sender_color", senderColor.ToString());

            var result = await command.ExecuteScalarAsync();
            if (result is int userId)
            {
                Console.WriteLine($"Benutzer-ID für '{sender}' ist {userId}.");
                return userId;
            }

            throw new InvalidOperationException("Benutzer-ID konnte nicht abgerufen werden.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Überprüfen oder Hinzufügen des Benutzers: {ex.Message}");
            throw;
        }


    }

    private async Task<List<ChatMessage>> GetChatHistoryFromDatabase(DateTime? startDate = null, DateTime? endDate = null, string? username = null, int limit = 0)
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

            // Filter hinzufügen
            if (startDate.HasValue)
            {
                query.Append(" AND Chat.timestamp >= @startDate");
                parameters.Add(new NpgsqlParameter("startDate", startDate.Value));
            }

            if (endDate.HasValue)
            {
                query.Append(" AND Chat.timestamp <= @endDate");
                parameters.Add(new NpgsqlParameter("endDate", endDate.Value));
            }

            if (!string.IsNullOrEmpty(username))
            {
                query.Append(" AND Benutzer.Sender = @username");
                parameters.Add(new NpgsqlParameter("username", username));
            }

            // Sortierung und Limitierung
            query.Append(" ORDER BY Chat.timestamp");
            if (limit > 0)
            {
                query.Append(" DESC LIMIT @limit");
                parameters.Add(new NpgsqlParameter("limit", limit));
            }

            // SQL-Befehl vorbereiten und ausführen
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

    private async Task<List<ChatMessage>> GetChatHistoryFromDatabase(string? username, int limit)
    {
        var messages = new List<ChatMessage>();

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // SQL-Abfrage mit dynamischem Limit und optionalem Benutzernamen
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

            if (limit > 0)
            {
                query.Append(" ORDER BY Chat.timestamp DESC LIMIT @limit");
                parameters.Add(new NpgsqlParameter("limit", limit));
            }
            else
            {
                query.Append(" ORDER BY Chat.timestamp");
            }

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
}
