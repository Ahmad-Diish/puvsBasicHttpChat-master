using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Client
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var serverUri = new Uri("http://localhost:5000");

            // query the user for a name
            Console.Write("Geben Sie Ihren Namen ein: ");
            var sender = Console.ReadLine() ?? Guid.NewGuid().ToString();
            Console.WriteLine();

            // create a new client and connect the event handler for the received messages
            var client = new ChatClient(sender, serverUri);
            client.MessageReceived += MessageReceivedHandler;

            // Benutzerfreundliche Benutzeroberfläche
            while (true)
            {
                Console.WriteLine("\n--- Hauptmenü ---");
                Console.WriteLine("1. Neuen Chat beginnen");
                Console.WriteLine("2. Vorherigen Chat-Verlauf fortsetzen");
                Console.WriteLine("3. Chat-Verlauf verwalten");
                Console.WriteLine("4. Chat schließen");
                Console.Write("Ihre Wahl: ");
                var choice = Console.ReadLine();

                if (choice == "1")
                {
                    // Startet einen neuen Chat
                    await StartChat(client, isNewChat: true);
                }
                else if (choice == "2")
                {
                    // Fortsetzt den vorherigen Chat-Verlauf
                    await StartChat(client, isNewChat: false);
                }
                else if (choice == "3")
                {
                    // Chat-Verlauf verwalten (Anzeigen, Löschen, Filtern)
                    await HandleChatHistory(client);
                }
                else if (choice == "4")
                {
                    // Chat schließen
                    Console.WriteLine("Chat wird geschlossen...");
                    break;
                }
                else
                {
                    Console.WriteLine("Ungültige Auswahl, bitte versuchen Sie es erneut.");
                }
            }
        }

        /// <summary>
        /// Starts a new chat session or continues the previous one.
        /// </summary>
        /// <param name="client">The ChatClient instance.</param>
        /// <param name="isNewChat">True if the user wants to start a new chat; false to continue the previous chat.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task StartChat(ChatClient client, bool isNewChat)
        {
            if (!isNewChat)
            {
                // Laden des vorherigen Verlaufs und Fortsetzung
                Console.WriteLine("\nFortsetzen des vorherigen Chat-Verlaufs...");
                await client.LoadAndDisplayPreviousMessages();
            }

            // connect to the server and start listening for messages
            var connectTask = await client.Connect();
            var listenTask = client.ListenForMessages();

            if (connectTask)
            {
                Console.WriteLine("Sie sind nun verbunden. Sie können Nachrichten senden.");
            }
            else
            {
                Console.WriteLine("Verbindung konnte nicht hergestellt werden.");
                return;
            }

            // query the user for messages to send or the exit command
            while (true)
            {
                Console.Write("Geben Sie Ihre Nachricht ein (oder 'exit' zum Beenden): ");
                var content = Console.ReadLine() ?? string.Empty;

                if (content.ToLower() == "exit")
                {
                    client.CancelListeningForMessages();
                    break;
                }

                Console.WriteLine($"Senden der Nachricht: {content}");

                if (await client.SendMessage(content))
                {
                    Console.WriteLine("Nachricht erfolgreich gesendet.");
                }
                else
                {
                    Console.WriteLine("Nachricht konnte nicht gesendet werden.");
                }
            }

            await Task.WhenAll(listenTask);

            Console.WriteLine("\nAuf Wiedersehen...");
        }

        /// <summary>
        /// Handles all operations related to the chat history (view, delete, filter by room and time).
        /// </summary>
        /// <param name="client">The ChatClient instance.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task HandleChatHistory(ChatClient client)
        {
            while (true)
            {
                Console.WriteLine("\n--- Verlauf Optionen ---");
                Console.WriteLine("1. Chat-Verlauf anzeigen");
                Console.WriteLine("2. Chat-Verlauf löschen");
                Console.WriteLine("3. Chat-Verlauf nach Raum und Zeit anzeigen");
                Console.WriteLine("4. Zurück zum Hauptmenü");
                Console.Write("Ihre Wahl: ");
                var choice = Console.ReadLine();

                if (choice == "1")
                {
                    // Standardmäßig den Verlauf für den Standardraum anzeigen
                    await client.LoadAndDisplayPreviousMessages();
                }
                else if (choice == "2")
                {
                    // Den Chat-Verlauf des aktuellen Benutzers löschen
                    client.DeleteChatHistory();
                }
                else if (choice == "3")
                {
                    Console.Write("Geben Sie das Startdatum (dd.MM.yyyy) ein: ");
                    var startDateInput = Console.ReadLine();
                    Console.Write("Geben Sie das Enddatum (dd.MM.yyyy) ein: ");
                    var endDateInput = Console.ReadLine();

                    if (DateTime.TryParse(startDateInput, out DateTime startDate) && DateTime.TryParse(endDateInput, out DateTime endDate))
                    {
                        await client.LoadMessagesByDateRange(startDate, endDate);
                    }
                    else
                    {
                        Console.WriteLine("Ungültiges Datum eingegeben.");
                    }
                }
                else if (choice == "4")
                {
                    // Zurück zum Hauptmenü
                    break;
                }
                else
                {
                    Console.WriteLine("Ungültige Auswahl, bitte versuchen Sie es erneut.");
                }
            }
        }

        /// <summary>
        /// Helper method to display the newly received messages.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="MessageReceivedEventArgs"/> instance containing the event data.</param>
        static void MessageReceivedHandler(object? sender, MessageReceivedEventArgs e)
        {
            Console.WriteLine($"\nNeue Nachricht von {e.Sender}: {e.Message}  [{e.Timestamp}]");
        }
    }
}
