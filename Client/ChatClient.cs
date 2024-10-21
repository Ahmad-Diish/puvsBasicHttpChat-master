using System.Net.Http.Json;
using Data;

namespace Client;


/// A client for the simple web server
public class ChatClient
{

    /// The HTTP client to be used throughout
    private readonly HttpClient httpClient;

    /// The alias of the user
    private readonly string alias;


    /// The cancellation token source for the listening task
    readonly CancellationTokenSource cancellationTokenSource = new();

    /// Initializes a new instance of the <see cref="ChatClient"/> class.
    /// </summary>
    /// <param name="alias">The alias of the user.</param>
    /// <param name="serverUri">The server URI.</param>
    public ChatClient(string alias, Uri serverUri)
    {
        this.alias = alias;
        this.httpClient = new HttpClient();
        this.httpClient.BaseAddress = serverUri;
    }


    /// Connects this client to the server.
    /// <returns>True if the connection could be established; otherwise False</returns>
    public async Task<bool> Connect()
    {
        // create and send a welcome message
        var message = new ChatMessage { Sender = this.alias, Content = $"Hi, I joined the chat!" };
        var response = await this.httpClient.PostAsJsonAsync("/messages", message);

        return response.IsSuccessStatusCode;
    }

    /// Sends a new message into the chat.
    /// <param name="content">The message content as text.</param>
    /// <returns>True if the message could be send; otherwise False</returns>
    public async Task<bool> SendMessage(string content)
    {
        // creates the message and sends it to the server
        var message = new ChatMessage { Sender = this.alias, Content = content, Timestamp = DateTime.Now };
        var response = await this.httpClient.PostAsJsonAsync("/messages", message);

        // Speichere die Nachricht in der Datei
        // SaveMessageToFile(message);

        return response.IsSuccessStatusCode;
    }

    /// Listens for messages until this process is cancelled by the user.
    public async Task ListenForMessages()
    {
        var cancellationToken = this.cancellationTokenSource.Token;

        // run until the user request the cancellation
        while (true)
        {
            try
            {
                // listening for messages. possibly waits for a long time.
                var message = await this.httpClient.GetFromJsonAsync<ChatMessage>($"/messages?id={this.alias}", cancellationToken);

                // if a new message was received notify the user
                if (message != null)
                {
                    // Speichere empfangene Nachrichten in der Datei
                    SaveMessageToFile(message);

                    this.OnMessageReceived(message.Sender, message.Content, message.Timestamp);
                }
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // catch the cancellation 
                this.OnMessageReceived("Me", "Leaving the chat", DateTime.Now);
                break;
            }
        }
    }
    /// Cancels the loop for listening for messages.

    public void CancelListeningForMessages()
    {
        // signal the cancellation request
        this.cancellationTokenSource.Cancel();
    }

    // Enabled the user to receive new messages. The assigned delegated is called when a new message is received.
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;


    /// Called when a message was received and signal this to the user using the MessageReceived event.
    /// <param name="sender">The alias of the sender.</param>
    /// <param name="message">The containing message as text.</param>
    protected virtual void OnMessageReceived(string sender, string message, DateTime timestamp)
    {
        this.MessageReceived?.Invoke(this, new MessageReceivedEventArgs
        {
            Sender = sender,
            Message = message,
            Timestamp = timestamp
        });
    }



    // Chat-Verläufe 

    /// <summary>
    /// Saves the chat messages to a file within the "Verlauf" folder to preserve the chat history.
    /// </summary>
    /// <param name="message">The chat message to be saved.</param>
    private void SaveMessageToFile(ChatMessage message)
    {
        // Define the folder and file path for storing the chat history
        string folderPath = "Verlauf";
        string logFilePath = Path.Combine(folderPath, $"{this.alias}_chat_history.txt");

        // Ensure the "Verlauf" folder exists; if not, create it
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        // Using StreamWriter to append the message to the chat history file
        using (StreamWriter writer = new StreamWriter(logFilePath, true))
        {
            writer.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
        }
    }

    /// <summary>
    /// Deletes the chat history file for the current user.
    /// </summary>
    public void DeleteChatHistory()
    {
        string folderPath = "Verlauf";
        string logFilePath = Path.Combine(folderPath, $"{this.alias}_chat_history.txt");

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


    /// <summary>
    /// Loads and displays the chat messages from a specific date range (ignoring time).
    /// </summary>
    /// <param name="startDate">The start date of the range.</param>
    /// <param name="endDate">The end date of the range.</param>
    public async Task LoadMessagesByDateRange(DateTime startDate, DateTime endDate)
    {
        List<ChatMessage> messages = await Task.Run(() => LoadPreviousMessagesFromFile());

        // Filter messages based on the date range, ignoring the time part
        var filteredMessages = messages
            .Where(m => m.Timestamp.Date >= startDate.Date && m.Timestamp.Date <= endDate.Date)
            .ToList();

        if (filteredMessages.Count > 0)
        {
            Console.WriteLine($"\nChatverlauf vom {startDate.ToShortDateString()} bis {endDate.ToShortDateString()}:");
            foreach (var message in filteredMessages)
            {
                // Display only the date part of the timestamp
                Console.WriteLine($"{message.Timestamp.Date.ToShortDateString()}: {message.Sender}: {message.Content}");
            }
        }
        else
        {
            Console.WriteLine("Keine Nachrichten in diesem Zeitraum gefunden.");
        }
    }



    /// <summary>
    /// Loads and displays the previously saved chat messages from the file asynchronously.
    /// </summary>
    public async Task LoadAndDisplayPreviousMessages()
    {
        List<ChatMessage> messages = await Task.Run(() => LoadPreviousMessagesFromFile());

        if (messages.Count > 0)
        {
            Console.WriteLine("\nPrevious chat history:");
            foreach (var message in messages)
            {
                // Displaying each message in the format of timestamp, sender, and content
                Console.WriteLine($"{message.Timestamp}: {message.Sender}: {message.Content}");
            }
        }
        else
        {
            // If no messages are found in the chat history file
            Console.WriteLine("No chat history available.");
        }
    }

    /// <summary>
    /// Reads the chat history from the file located in the "Verlauf" folder.
    /// </summary>
    /// <returns>A list of chat messages.</returns>
    private List<ChatMessage> LoadPreviousMessagesFromFile()
    {
        List<ChatMessage> messages = new List<ChatMessage>();

        // Define the folder and file path for reading the chat history
        string folderPath = "Verlauf";
        string logFilePath = Path.Combine(folderPath, $"{this.alias}_chat_history.txt");

        // Checking if the chat history file exists
        if (File.Exists(logFilePath))
        {
            // Reading each line of the file and extracting the timestamp, sender, and message content
            string[] lines = File.ReadAllLines(logFilePath);
            foreach (string line in lines)
            {
                string[] parts = line.Split(new[] { ": " }, 3, StringSplitOptions.None);
                if (parts.Length == 3)
                {
                    ChatMessage message = new ChatMessage
                    {
                        Timestamp = DateTime.Parse(parts[0]),
                        Sender = parts[1],
                        Content = parts[2]
                    };
                    messages.Add(message);
                }
            }
        }

        return messages;
    }

}