using System.Collections.Concurrent;
using Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
                }

                Console.WriteLine($"Nachricht vom Client empfangen: {message!.Content}");

                // maintain the chat history
                this.messageQueue.Enqueue(message);

                // broadcast the new message to all registered clients
                lock (this.lockObject)
                {
                    foreach (var (id, client) in this.waitingClients)
                    {
                        Console.WriteLine($"Broadcasting an Client '{id}'");

                        // possible memory leak as the 'dead' clients are never removed from the list
                        client.TrySetResult(message);
                    }
                }

                Console.WriteLine($"Nachricht an alle Clients gesendet: {message.Content}");

                // confirm that the new message was successfully processed
                context.Response.StatusCode = StatusCodes.Status201Created;
                await context.Response.WriteAsync("Nachricht empfangen und verarbeitet.");
            });
        });
    }
}
