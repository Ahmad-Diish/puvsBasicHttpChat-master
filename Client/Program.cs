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

            // Query the user for a name
            Console.Write("Geben Sie Ihren Namen ein: ");
            var sender = (Console.ReadLine() ?? Guid.NewGuid().ToString()).ToLower();

            Console.WriteLine();

            // Create the main client instance
            client = new ChatClient(sender, serverUri);

            // Check for name and color conflicts
            var error = false;
            HttpStatusCode checkResult = await client.Check();

            if (checkResult == HttpStatusCode.BadRequest)
            {
                Console.WriteLine("Ein Name ist erforderlich.");
                error = true;
            }
            else if (checkResult == HttpStatusCode.Conflict)
            {
                Console.WriteLine("Dieser Benutzername ist bereits vergeben. Bitte versuchen Sie es mit einem anderen Namen.");
                Environment.Exit(0);
            }

            if (error) return;

            // Main menu loop
            while (true)
            {
                Console.ResetColor();
                Console.WriteLine("\n--- Hauptmenü ---");
                Console.WriteLine("1. Neuen Chat beginnen");
                Console.WriteLine("2. Vorherigen Chat-Verlauf fortsetzen");
                Console.WriteLine("3. Chat-Verlauf verwalten");
                Console.WriteLine("4. Chat schließen");
                Console.Write("Ihre Wahl: ");
                var choice = Console.ReadLine();

                // Create new client instance only when starting a chat
                if (choice == "1" || choice == "2")
                {
                    client = new ChatClient(sender, serverUri);
                    client.MessageReceived += MessageReceivedHandler;
                }

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
                        Console.WriteLine("Chat wird geschlossen...");
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
                Console.WriteLine("\nFortsetzen des vorherigen Chat-Verlaufs...");
                await client.LoadAndDisplayPreviousMessages();
            }

            var connectTask = await client.Connect();
            if (!connectTask)
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
                    Console.WriteLine("\nGeben Sie bitte einfach Ihre '(Nachricht)' ein , oder '(exit)' zum Beenden: ");
                    Console.ForegroundColor = client.userColor;
                    content = Console.ReadLine() ?? string.Empty;
                    isInputting = false;
                    Console.ResetColor();
                }


                if (content.ToLower() == "exit")
                {
                    await client.Disconnect(); // Use the new Disconnect method instead of just cancelling
                    break;
                }
                Console.WriteLine($"Sending message: {content}");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    if (await client.SendMessage(content))
                    {
                        lock (lockObjectMessageReceivedHandler)
                        {
                            Console.ResetColor();
                            //Console.WriteLine("Nachricht erfolgreich gesendet.");
                        }
                    }
                    else
                    {
                        lock (lockObjectMessageReceivedHandler)
                        {
                            Console.ResetColor();
                            Console.WriteLine("Nachricht konnte nicht gesendet werden.");
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
                Console.WriteLine("3. Chat-Verlauf nach Raum und Zeit anzeigen");
                Console.WriteLine("4. Zurück zum Hauptmenü");
                Console.Write("Ihre Wahl: ");
                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        await client.LoadAndDisplayPreviousMessages();
                        break;
                    case "2":
                        client.DeleteChatHistory();
                        break;
                    case "3":
                        Console.Write("Geben Sie das Startdatum (dd.MM.yyyy) ein: ");
                        var startDateInput = Console.ReadLine();
                        Console.Write("Geben Sie das Enddatum (dd.MM.yyyy) ein: ");
                        var endDateInput = Console.ReadLine();

                        if (DateTime.TryParse(startDateInput, out DateTime startDate) &&
                            DateTime.TryParse(endDateInput, out DateTime endDate))
                        {
                            await client.LoadMessagesByDateRange(startDate, endDate);
                        }
                        else
                        {
                            Console.WriteLine("Ungültiges Datum eingegeben.");
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
            string formattedTime = currentTime.ToString("HH:mm:ss");

            lock (lockObjectMessageReceivedHandler)
            {
                if (isInputting)
                {
                    Console.WriteLine(); // Add a new line if user is currently inputting
                }
                Console.ResetColor();
                Console.Write($"\nNeue Nachricht empfangen von: ");
                Console.ForegroundColor = e.UsernameColor;
                Console.Write($"{e.Sender}: {e.Message} um {formattedTime}\n");
                Console.ResetColor();
                if (isInputting)
                {
                    Console.ForegroundColor = client.userColor;
                }
            }
        }
    }
}
