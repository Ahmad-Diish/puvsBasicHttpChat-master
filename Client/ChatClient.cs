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
        var message = new ChatMessage { Sender = this.alias, Content = content };
        var response = await this.httpClient.PostAsJsonAsync("/messages", message);

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
                    this.OnMessageReceived(message.Sender, message.Content);
                }
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // catch the cancellation 
                this.OnMessageReceived("Me", "Leaving the chat");
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
    protected virtual void OnMessageReceived(string sender, string message)
    {
        this.MessageReceived?.Invoke(this, new MessageReceivedEventArgs { Sender = sender, Message = message });
    }
}