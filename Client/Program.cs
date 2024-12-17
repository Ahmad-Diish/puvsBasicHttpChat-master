using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Client
{
    public static class Program
    {
        private static ChatClient client = new ChatClient("default", new Uri("http://localhost:5000"));

        private static readonly object lockObjectMessageReceivedHandler = new();
        private static readonly object lockObjectWelcome = new();
        private static bool isInputting = false;

        public static async Task Main(string[] args)
        {
            var serverUri = new Uri("http://localhost:5000");

            while (true)
            {
                // Query the user for a name
                Console.Write("Geben Sie Ihren Namen ein: ");
                var sender = Console.ReadLine()?.Trim();

                if (string.IsNullOrWhiteSpace(sender))
                {
                    Console.WriteLine("Der Name darf nicht leer sein. Bitte erneut versuchen.");
                    continue;
                }

                Console.WriteLine();

                // Assign the new client instance to the static client variable
                client = new ChatClient(sender, serverUri);

                // Check for name and color conflicts
                var registrationSuccess = await client.Check();

                if (registrationSuccess)
                {
                    Console.WriteLine($"Willkommen, {sender}!");
                    break;
                }
            }

            // Add the message received handler
            client.MessageReceived += MessageReceivedHandler;

            // Main menu loop
            while (true)
            {
                Console.ResetColor();
                Console.WriteLine("\n--- Hauptmenü ---");
                Console.WriteLine("1. Vorherigen Privat-Chat-Verlauf fortsetzen");
                Console.WriteLine("2. Vorherigen Allgemein-Chat-Verlauf fortsetzen");
                Console.WriteLine("3. Chat-Verlauf verwalten");
                Console.WriteLine("4. Statistik anzeigen");
                Console.WriteLine("5. Chat schließen");
                Console.Write("Ihre Wahl: ");
                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        await StartChat(client, true);
                        break;
                    case "2":
                        await StartChat(client, false);
                        break;
                    case "3":
                        await HandleChatHistory(client);
                        break;
                    case "4":
                        await client.SendMessage("/statistik");
                        break;
                    case "5":
                        Console.WriteLine("Chat wird geschlossen...");
                        await client.Disconnect();
                        return;
                    default:
                        Console.WriteLine("Ungültige Auswahl, bitte versuchen Sie es erneut.");
                        break;
                }
            }
        }

        public static async Task StartChat(ChatClient client, bool isNewChat)
        {
            if (!isNewChat)
            {
                Console.WriteLine("\nFortsetzen des vorherigen Allgemein-Chat-Verlaufs...");
                await client.LoadAndDisplayPreviousMessages();
            }

            if (isNewChat)
            {
                Console.WriteLine("\nFortsetzen des vorherigen Privat-Chat-Verlaufs...");
                await client.LoadAndDisplayAllMessagesForCurrentUser();
            }

            var connectSuccess = await client.Connect();
            if (!connectSuccess)
            {
                Console.WriteLine("Verbindung konnte nicht hergestellt werden.");
                return;
            }


            var listenTask = client.ListenForMessages();
            Console.WriteLine("Sie sind nun verbunden. Sie können Nachrichten senden.");

            while (true)
            {
                string content;
                lock (lockObjectWelcome)
                {
                    isInputting = true;
                    Console.ResetColor();
                    Console.WriteLine("\nGeben Sie Ihre Nachricht ein oder 'exit' zum Beenden: ");
                    Console.ForegroundColor = client.userColor;
                    content = Console.ReadLine() ?? string.Empty;
                    isInputting = false;
                    Console.ResetColor();
                }

                if (content.ToLower() == "exit")
                {
                    await client.Disconnect();
                    break;
                }

                
                 Console.Write("Nachricht senden: ");
                 Console.ForegroundColor = client.userColor;
                 Console.WriteLine(content);
                 Console.ResetColor();

                if (!string.IsNullOrWhiteSpace(content))
                {
                    if (await client.SendMessage(content))
                    {
                        lock (lockObjectMessageReceivedHandler)
                        {
                            Console.ResetColor();
                            // Optionally, you can add a confirmation message here if desired
                        }
                    }
                    else
                    {
                        lock (lockObjectMessageReceivedHandler)
                        {
                            Console.ResetColor();
                        }
                    }
                }
            }

            await listenTask;
            Console.WriteLine("\nAuf Wiedersehen...");
        }

        public static async Task HandleChatHistory(ChatClient client)
        {
            while (true)
            {
                Console.ResetColor();
                Console.WriteLine("\n--- Verlauf Optionen ---");
                Console.WriteLine("1. Chat-Verlauf anzeigen");
                Console.WriteLine("2. Chat-Verlauf löschen");
                Console.WriteLine("3. Chat-Verlauf der letzten XX Stunden anzeigen");
                Console.WriteLine("4. Zurück zum Hauptmenü");
                Console.Write("Ihre Wahl: ");
                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        await client.LoadAndDisplayPreviousMessages();
                        break;
                    case "2":
                        await client.DeleteChatHistory();
                        break;
                    case "3":
                        Console.Write("Geben Sie die Anzahl der Stunden für den Chat-Verlauf ein: ");
                        if (int.TryParse(Console.ReadLine(), out int hours))
                        {
                            await  client.LoadAndDisplayChatHistoryLastHours(hours);
                        }
                        else
                        {
                            Console.WriteLine("Ungültige Stundenangabe.");
                        }
                        break;
                    case "4":
                        return;
                    default:
                        Console.WriteLine("Ungültige Auswahl, bitte versuchen Sie es erneut.");
                        break;
                }
            }
        }

        static void MessageReceivedHandler(object? sender, MessageReceivedEventArgs e)
        {
            DateTime currentTime = DateTime.Now;
            string formattedTime = currentTime.ToString("HH:mm");

            lock (lockObjectMessageReceivedHandler)
            {
                if (isInputting)
                {
                    Console.WriteLine();
                }
                Console.ResetColor();
                if (client == null)
                {
                    throw new InvalidOperationException("Client is not initialized.");
                }

                if (e.Sender != client.Alias)
                {
                    Console.Write($"\nNeue Nachricht empfangen von: ");

                    Console.ForegroundColor = e.UsernameColor;
                    Console.Write($"{e.Sender}: {e.Message}  [{formattedTime}]\n");
                    Console.ResetColor();
                }

                if (isInputting)
                {
                    Console.ForegroundColor = client.userColor;
                }
            }
        }

    }
}
