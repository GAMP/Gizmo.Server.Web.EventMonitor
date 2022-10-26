using Gizmo.Web.Api.Messaging;
using MessagePack;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Threading.Channels;

namespace Gizmo.Server.Web.EventMonitor
{
    /// <summary>
    /// Real time service.
    /// </summary>
    /// <remarks>
    /// Responsible of receiving real time messages and responding to them.
    /// </remarks>
    public class RealTimeService : IHostedService
    {
        #region CONSTRUCTOR
        /// <summary>
        /// Creates new instance.
        /// </summary>
        /// <param name="logger">Service logger.</param>
        public RealTimeService(ILogger<RealTimeService> logger,
            IOptions<RealTimeConfig> options,
            IOptions<AuthenticationConfig> authOptions)
        {
            _logger = logger;
            _options = options;
            _authOptions = authOptions;
        }
        #endregion

        #region CONSTANTS

        const int MAX_INPUT_COMMANDS = 10;
        const int RECONNECT_WAIT_TIME = 1000;

        #endregion

        #region FIELDS
        readonly Channel<IAPIEventMessage> _inputCommands = Channel.CreateBounded<IAPIEventMessage>(new BoundedChannelOptions(MAX_INPUT_COMMANDS)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        readonly ILogger<RealTimeService> _logger;
        readonly IOptions<RealTimeConfig> _options;
        private IOptions<AuthenticationConfig> _authOptions;
        readonly CancellationTokenSource _globalCts = new();
        HubConnection? _hubConnection;
        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets service logger.
        /// </summary>
        private ILogger<RealTimeService> Logger
        {
            get { return _logger; }
        }

        #endregion

        #region PUBLIC FUNCTIONS

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Logger.LogInformation("Starting service.");

            //create linked cancellation token source
            //this will be used in case start or global cancellation occurs 
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _globalCts.Token);

            //get current options
            var currentOptions = _options.Value;

            if (string.IsNullOrEmpty(currentOptions.RealTimeUrl))
                throw new ArgumentNullException(nameof(currentOptions.RealTimeUrl));

            //create uri builder
            UriBuilder uriBuilder = new(currentOptions.RealTimeUrl);

               //get effective real time url
            string effectiveUrl = uriBuilder.ToString();

            _hubConnection = new HubConnectionBuilder()
                .ConfigureLogging(l=>l.AddConsole())
                .AddMessagePackProtocol(options =>
                {
                    options.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithResolver(MessagePack.Resolvers.StandardResolver.Instance)
                    .WithSecurity(MessagePackSecurity.UntrustedData);
                })
                //.AddJsonProtocol(options =>
                //{
                //    options.AddConverters();
                //})
                .WithAutomaticReconnect()
                .WithUrl(effectiveUrl, opt =>
                {
                    //enable accept all certs
                    opt.WebSocketConfiguration = conf =>
                    {
                        conf.RemoteCertificateValidationCallback = (message, cert, chain, errors) => { return true; };
                    };
                    opt.HttpMessageHandlerFactory = factory => new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => { return true; }
                    };

                    //hook up access token provider here
                    opt.AccessTokenProvider = () => TokenProviderAsync();

                    //configure transfer options
                    opt.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                    opt.DefaultTransferFormat = Microsoft.AspNetCore.Connections.TransferFormat.Binary;
                })
                .Build();

            //add signalr method handlers
            _hubConnection.On("Event", (IAPIEventMessage eventMessage) => EventMessageHandler(eventMessage));

            //attach signalr client event handlers
            _hubConnection.Closed += OnConnectionClosed;
            _hubConnection.Reconnected += OnConnectionReconnected;
            _hubConnection.Reconnecting += OnConnectionReconnecting;

            _ = InputChannelReader(_globalCts.Token);

            _ = ConnectWithRetryAsync(linkedCts.Token);

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_hubConnection!=null && _hubConnection.State == HubConnectionState.Connected)
            {
                //close hub connection
                await _hubConnection.StopAsync(cancellationToken);
            }

            Logger.LogInformation("Stopping service.");

            //complete input channel
            _inputCommands.Writer.Complete();

            //cancel any pending operations
            _globalCts.Cancel();
        }

        #endregion

        #region EVENT HANDLERS

        private Task OnConnectionReconnecting(Exception? arg)
        {
            Logger.LogInformation("Reconnecting.");
            return Task.CompletedTask;
        }

        private Task OnConnectionReconnected(string? arg)
        {
            Logger.LogInformation("Reconnected.");
            return Task.CompletedTask;
        }

        private Task OnConnectionClosed(Exception? arg)
        {
            Logger.LogInformation("Connection closed.");
            return Task.CompletedTask;
        }

        #endregion

        #region PRIVATE FUNCTIONS

        private async Task ConnectWithRetryAsync(CancellationToken token)
        {
            // Keep trying untill connection established
            while (!token.IsCancellationRequested)
            {
                try
                {
                    //initate connection if connection is not currently attempted or already made
                    if (_hubConnection != null && _hubConnection.State != HubConnectionState.Connected && _hubConnection.State != HubConnectionState.Reconnecting)
                    {
                        await _hubConnection.StartAsync(token);
                        await _hubConnection.InvokeAsync("Join", "Meh");
                        await _hubConnection.InvokeAsync("Join", "Meh");
                        await _hubConnection.InvokeAsync("Leave", "Meh");
                  
                    }
                    else
                    {
                        //delay reconnection attempt
                        await Task.Delay(RECONNECT_WAIT_TIME, token);
                    }
                }
                catch when (token.IsCancellationRequested)
                {
                    //no need to rethrow since operation was canceled
                    //exit while
                    break;
                }
                catch (HttpRequestException)
                {
                    //this will be thrown on failed connection

                    //delay reconnection attempt
                    await Task.Delay(RECONNECT_WAIT_TIME, token);
                }
                catch (Exception ex)
                {
                    Logger.LogCritical("Connection failed.", ex);

                    //exit while
                    break;
                }
            }
        }

        private async Task EventMessageHandler(IAPIEventMessage commandMessage)
        {
            //write the message to the command channel
            await _inputCommands.Writer.WriteAsync(commandMessage);
        }

        private async Task InputChannelReader(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    //read command
                    var command = await _inputCommands.Reader.ReadAsync(cancellationToken);

                    Logger.LogInformation("EVENT: {eventType}", command);
                }
                catch (Exception exception) when (exception is ChannelClosedException || exception is OperationCanceledException)
                {
                    //channel was closed or operation was cancelled
                    break;
                }
                catch
                {
                    //some other error
                }
            }
        }

        /// <summary>
        /// Default token provider.
        /// </summary>
        /// <returns></returns>
        private async Task<string?> TokenProviderAsync()
        {
            using(var httpClient = new HttpClient())
            {
                var result = await httpClient.GetAsync($"http://localhost/auth/token?username={_authOptions.Value.Username}&password={_authOptions.Value.Password}");
                result.EnsureSuccessStatusCode();

                var token = await result.Content.ReadFromJsonAsync<JwtToken>();
                return token?.Token;

            }
        }

        private class JwtToken
        {
            public string? Token
            {
                get;set;
            }
        }

        #endregion
    }
}
