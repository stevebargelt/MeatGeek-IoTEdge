namespace Telemetry
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Runtime.Loader;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json;
    using Serilog;
    using Serilog.Configuration;
    using Serilog.Core;
    using Serilog.Events;

    class Program
    {
        static TimeSpan telemetryInterval { get; set; } = TimeSpan.FromSeconds(10);
        static string SessionID { get; set; }
        private static CancellationTokenSource _cts;
        static string deviceId {get; set; } 
        private static HttpClient _httpClient = new HttpClient();
        public static int Main() => MainAsync().Result;

        static async Task<int> MainAsync()
        {
            InitLogging();

            Log.Information($"Module {Environment.GetEnvironmentVariable("IOTEDGE_MODULEID")} starting up...");
            var moduleClient = await Init();
           
            _cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => _cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => _cts.Cancel();
            deviceId = Environment.GetEnvironmentVariable("IOTEDGE_DEVICEID");
            
            Twin currentTwinProperties = await moduleClient.GetTwinAsync();
            if (currentTwinProperties.Properties.Desired.Contains("TelemetryInterval"))
            {
                telemetryInterval = TimeSpan.FromSeconds((int)currentTwinProperties.Properties.Desired["TelemetryInterval"]);
            }
            if (currentTwinProperties.Properties.Desired.Contains("SessionId"))
            {
                SessionID = currentTwinProperties.Properties.Desired["SessionId"];
            }
            ModuleClient userContext = moduleClient;      
            await moduleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertiesUpdated, userContext);
            //await moduleClient.SetInputMessageHandlerAsync("control", ControlMessageHandle, userContext);
            await moduleClient.SetMethodHandlerAsync("SetTelemetryInterval", SetTelemetryInterval, userContext);
            await moduleClient.SetMethodHandlerAsync("SetSessionId", SetSessionId, userContext);
            await moduleClient.SetMethodHandlerAsync("EndSession", EndSession, userContext);
            
            await SendEvents(moduleClient, _cts.Token);

            // Wait until the app unloads or is cancelled
            await WhenCancelled(_cts.Token);
            return 0;        
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient
        /// </summary>
        static async Task<ModuleClient> Init()
        {
            var transportType = TransportType.Amqp_Tcp_Only;
            string transportProtocol = Environment.GetEnvironmentVariable("ClientTransportType");

            // The way the module connects to the EdgeHub can be controlled via the env variable. Either MQTT or AMQP
            if (!string.IsNullOrEmpty(transportProtocol))
            {
                switch (transportProtocol.ToUpper())
                {
                    case "AMQP":
                        transportType = TransportType.Amqp_Tcp_Only;
                        break;
                    case "MQTT":
                        transportType = TransportType.Mqtt_Tcp_Only;
                        break;
                    default:
                        // Anything else: use default
                        Log.Warning($"Ignoring unknown TransportProtocol={transportProtocol}. Using default={transportType}");
                        break;
                }
            }

            // Open a connection to the Edge runtime
            ModuleClient moduleClient = await ModuleClient.CreateFromEnvironmentAsync(transportType);
            moduleClient.SetConnectionStatusChangesHandler(ConnectionStatusHandler);

            await moduleClient.OpenAsync();
            Log.Information($"Edge Hub module client initialized using {transportType}");

            return moduleClient;
        }

        /// <summary>
        /// Callback for whenever the connection status changes
        /// Mostly we just log the new status and the reason. 
        /// But for some disconnects we need to handle them here differently for our module to recover
        /// </summary>
        /// <param name="status"></param>
        /// <param name="reason"></param>
        private static void ConnectionStatusHandler(ConnectionStatus status, ConnectionStatusChangeReason reason)
        {
            Log.Information($"Module connection changed. New status={status.ToString()} Reason={reason.ToString()}");

            // Sometimes the connection can not be recovered if it is in either of those states.
            // To solve this, we exit the module. The Edge Agent will then restart it (retrying with backoff)
            if (reason == ConnectionStatusChangeReason.Retry_Expired || reason == ConnectionStatusChangeReason.Client_Close)
            {
                Log.Error($"Connection can not be re-established. Exiting module");
                _cts?.Cancel();
            }
        }

        /// <summary>
        /// Module behavior:
        ///        Sends data periodically (with default frequency of 5 seconds).
        /// </summary>
        static async Task SendEvents(ModuleClient moduleClient, CancellationToken cancellationToken)
        {
            // Read ModuleId from env
            string moduleId = Environment.GetEnvironmentVariable("IOTEDGE_MODULEID");
             
            int count = 1;
            string url = "http://localhost:5000/api/status";
            string json;
            while (!cancellationToken.IsCancellationRequested)
            {
                var correlationId = Guid.NewGuid().ToString();
                Log.Information($"New status message - CorrelationId={correlationId}");
                
                using (HttpResponseMessage response = _httpClient.GetAsync(url).Result)
                {
                    using (HttpContent content = response.Content)
                    {
                        json = content.ReadAsStringAsync().Result;
                    }
                }

                // Log.Information($"Device sending Event/Telemetry to IoT Hub...");
                SmokerStatus status = JsonConvert.DeserializeObject<SmokerStatus>(await _httpClient.GetStringAsync("http://localhost:5000/api/status"));
                if (!string.IsNullOrEmpty(SessionID)) 
                {
                   status.SessionId = SessionID;
                }
                status.SmokerId = deviceId;
                status.Type = "status";
                
                json = JsonConvert.SerializeObject(status);
                //Log.Information($"Device sending Event/Telemetry to IoT Hub| SmokerStaus.SmokerId = {status.SmokerId}, SmokerStaus.Type = {status.Type} || {json}");
                Message eventMessage = new Message(Encoding.UTF8.GetBytes(json));
                eventMessage.ContentType = "application/json";
                eventMessage.ContentEncoding = "UTF-8";
                eventMessage.Properties.Add("correlationId", correlationId);
                eventMessage.Properties.Add("sequenceNumber", count.ToString());
                eventMessage.Properties.Add("SessionId", SessionID);
                
                try
                {
                    await moduleClient.SendEventAsync("output1", eventMessage);
                    //telemetry.TrackEvent("81-Heartbeat-Sent-MessageForwarder", telemetryProperties);
                    //Log.Information("Smoker Status message sent");
                    Log.Information($"Telemetry sent | SmokerStaus.SmokerId = {status.SmokerId}, SmokerStaus.Type = {status.Type} || {json}");

                }
                catch (Exception e)
                {
                    Log.Error(e, "Error during message sending to Edge Hub");
                    //telemetry.TrackEvent("85-ErrorHeartbeatMessageNotSentToEdgeHub", telemetryProperties);
                }

                count++;
                await Task.Delay(telemetryInterval);
            }

        }

        static async Task OnDesiredPropertiesUpdated(TwinCollection desiredPropertiesPatch, object userContext)
        {
            // Log.Information("Desired property change:");
            // Log.Information(JsonConvert.SerializeObject(desiredPropertiesPatch));

            var reportedProperties = new TwinCollection();

            // At this point just update the configure configuration.
            if (desiredPropertiesPatch.Contains("TelemetryInterval"))
            {
                telemetryInterval = TimeSpan.FromSeconds((int)desiredPropertiesPatch["TelemetryInterval"]);
                reportedProperties["TelemetryInterval"] = telemetryInterval;                
            }
            if (desiredPropertiesPatch.Contains("SessionId"))
            {
                SessionID = desiredPropertiesPatch["SessionId"];
                reportedProperties["SessionId"] = SessionID;
            }            
            var moduleClient = (ModuleClient)userContext;
            await moduleClient.UpdateReportedPropertiesAsync(reportedProperties).ConfigureAwait(false);
        }

        private static Task<MethodResponse> SetTelemetryInterval(MethodRequest methodRequest, object userContext)
        {
            var data = Encoding.UTF8.GetString(methodRequest.Data);

            int newTelemetryInterval;
            // Check the payload is a single integer value
            if (Int32.TryParse(data, out newTelemetryInterval))
            {
                telemetryInterval = TimeSpan.FromSeconds(newTelemetryInterval);
                Log.Information($"Telemetry interval set to {data} seconds");
                string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
            }
            else
            {
                Log.Warning("SetTelemetryLevel Error: Invalid Parameter");
                string result = "{\"result\":\"Invalid parameter\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
            }
        }

        private static Task<MethodResponse> SetSessionId(MethodRequest methodRequest, object userContext)
        {
            var data = Encoding.UTF8.GetString(methodRequest.Data);
            
            if (!string.IsNullOrEmpty(data))
            {
                var sessionID = data.Replace("\"", "");
                SessionID = sessionID;
                Log.Information($"SessionID set to {sessionID}");
                // Acknowlege the direct method call with a 200 success message
                string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
            }
            else
            {
                Log.Warning("SetTelemetryLevel Error: Invalid Parameter");
                string result = "{\"result\":\"Payload Missing. Need the SessionId\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
            }
        }

        private static Task<MethodResponse> EndSession(MethodRequest methodRequest, object userContext)
        {
            var data = Encoding.UTF8.GetString(methodRequest.Data);

            SessionID = "";
            Log.Information($"Session Ended");
            string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
        }


        /// <summary>
        /// Initialize logging using Serilog
        /// LogLevel can be controlled via RuntimeLogLevel env var
        /// </summary>
        private static void InitLogging()
        {
            LoggerConfiguration loggerConfiguration = new LoggerConfiguration();

            var logLevel = Environment.GetEnvironmentVariable("RuntimeLogLevel");
            logLevel = !string.IsNullOrEmpty(logLevel) ? logLevel.ToLower() : "info";

            // set the log level
            switch (logLevel)
            {
                case "fatal":
                    loggerConfiguration.MinimumLevel.Fatal();
                    break;
                case "error":
                    loggerConfiguration.MinimumLevel.Error();
                    break;
                case "warn":
                    loggerConfiguration.MinimumLevel.Warning();
                    break;
                case "info":
                    loggerConfiguration.MinimumLevel.Information();
                    break;
                case "debug":
                    loggerConfiguration.MinimumLevel.Debug();
                    break;
                case "verbose":
                    loggerConfiguration.MinimumLevel.Verbose();
                    break;
            }

            // set logging sinks
            loggerConfiguration.WriteTo.Console(outputTemplate: "<{Severity}> {Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] - {Message}{NewLine}{Exception}");
            loggerConfiguration.Enrich.With(SeverityEnricher.Instance);
            loggerConfiguration.Enrich.FromLogContext();
            Log.Logger = loggerConfiguration.CreateLogger();
            Log.Information($"Initializied logger with log level {logLevel}");
        }


    public class SmokerStatus
    {
        [JsonProperty("id")] 
        public string Id { get; set; }
        [JsonProperty] 
        public int? ttl { get; set; }
        [JsonProperty("smokerId")] 
        public string SmokerId { get; set; }
        [JsonProperty("sessionId")] 
        public string SessionId { get; set; }
        [JsonProperty("type")] 
        public string Type { get; set; }
        [JsonProperty("augerOn")] 
        public bool AugerOn { get; set; }
        [JsonProperty("blowerOn")] 
        public bool BlowerOn { get; set; }
        [JsonProperty("igniterOn")] 
        public bool IgniterOn { get; set; }
        [JsonProperty("temps")] 
        public Temps Temps { get; set; }
        [JsonProperty("fireHealthy")] 
        public bool FireHealthy { get; set; }
        [JsonProperty("mode")] 
        public string Mode { get; set; }
        [JsonProperty("setPoint")] 
        public int SetPoint { get; set; }
        [JsonProperty("modeTime")] 
        public DateTime ModeTime { get; set; }
        [JsonProperty("currentTime")] 
        public DateTime CurrentTime { get; set; }
    }
    public class Temps
    {
        [JsonProperty("grillTemp")] 
        public double GrillTemp { get; set; }
        [JsonProperty("probe1Temp")] 
        public double Probe1Temp { get; set; }
        [JsonProperty("probe2Temp")] 
        public double Probe2Temp { get; set; }
        [JsonProperty("probe3Temp")] 
        public double Probe3Temp { get; set; }
        [JsonProperty("probe4Temp")] 
        public double Probe4Temp { get; set; }
    }

    }

    // This maps the Edge log level to the severity level based on Syslog severity levels.
    // https://en.wikipedia.org/wiki/Syslog#Severity_level
    // This allows tools to parse the severity level from the log text and use it to enhance the log
    // For example errors can show up as red
    class SeverityEnricher : ILogEventEnricher
    {
        static readonly IDictionary<LogEventLevel, int> LogLevelSeverityMap = new Dictionary<LogEventLevel, int>
        {
            [LogEventLevel.Fatal] = 0,
            [LogEventLevel.Error] = 3,
            [LogEventLevel.Warning] = 4,
            [LogEventLevel.Information] = 6,
            [LogEventLevel.Debug] = 7,
            [LogEventLevel.Verbose] = 7
        };

        SeverityEnricher()
        {
        }

        public static SeverityEnricher Instance => new SeverityEnricher();

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
                "Severity", LogLevelSeverityMap[logEvent.Level]));
    }    

}