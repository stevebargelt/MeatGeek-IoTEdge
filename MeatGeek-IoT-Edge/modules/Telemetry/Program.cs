namespace Telemetry
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Extensions.Configuration;
    using Newtonsoft.Json;
  

    class Program
    {
        static TimeSpan telemetryInterval { get; set; } = TimeSpan.FromSeconds(10);
        static string deviceId {get; set; } 
        private static HttpClient _httpClient = new HttpClient();
        static readonly Guid BatchId = Guid.NewGuid();
        public static int Main() => MainAsync().Result;

        static async Task<int> MainAsync()
        {
            Console.WriteLine("Telemetry Main() started.");

            IConfiguration configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("config/appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            Console.WriteLine(
                $"Initializing telemetry to send SmokerStatus "
                + $"messages, at an interval of {telemetryInterval.TotalSeconds} seconds."
                + $"more message here... ");

            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            ITransportSettings[] settings = { mqttSetting };

            // Open a connection to the Edge runtime
            ModuleClient moduleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await moduleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");
            //await moduleClient.SetMethodHandlerAsync("reset", ResetMethod, null);

            //(CancellationTokenSource cts, ManualResetEventSlim completed, Option<object> handler) = ShutdownHandler.Init(TimeSpan.FromSeconds(5), null);

            deviceId = Environment.GetEnvironmentVariable("IOTEDGE_DEVICEID");
            //moduleId = "IOTEDGE_MODULEID"
            Twin currentTwinProperties = await moduleClient.GetTwinAsync();
            if (currentTwinProperties.Properties.Desired.Contains("TelemetryInterval"))
            {
                telemetryInterval = TimeSpan.FromSeconds((int)currentTwinProperties.Properties.Desired["TelemetryInterval"]);
            }
            ModuleClient userContext = moduleClient;      
            await moduleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertiesUpdated, userContext);
            //await moduleClient.SetInputMessageHandlerAsync("control", ControlMessageHandle, userContext);
            await SendEvents(moduleClient);
            //await cts.Token.WhenCanceled();

            //completed.Set();
            //handler.ForEach(h => GC.KeepAlive(h));
            Console.WriteLine("Telemetry Main() finished.");
            return 0;
        }

        // static Task<MethodResponse> ResetMethod(MethodRequest methodRequest, object userContext)
        // {
        //     Console.WriteLine("Received direct method call to reset temperature sensor...");
        //     Reset = true;
        //     var response = new MethodResponse((int)HttpStatusCode.OK);
        //     return Task.FromResult(response);
        // }

        /// <summary>
        /// Module behavior:
        ///        Sends data periodically (with default frequency of 5 seconds).
        /// </summary>
        static async Task SendEvents(ModuleClient moduleClient)
        {
            Console.WriteLine("Send Events... calling API /status...");
            
            int count = 1;
            string url = "http://localhost:5000/api/status";
            string json;
            while (true)
            {
                using (HttpResponseMessage response = _httpClient.GetAsync(url).Result)
                {
                    using (HttpContent content = response.Content)
                    {
                        json = content.ReadAsStringAsync().Result;
                    }
                }

                Console.WriteLine($"Device sending Event/Telemetry to IoT Hub...");
                SmokerStatus status = JsonConvert.DeserializeObject<SmokerStatus>(await _httpClient.GetStringAsync("http://localhost:5000/api/status"));
                status.SmokerId = deviceId;
                status.PartitionKey = $"{status.SmokerId}-{DateTime.UtcNow:yyyy-MM}";
                json = JsonConvert.SerializeObject(status);
                Message eventMessage = new Message(Encoding.UTF8.GetBytes(json));
                eventMessage.Properties.Add("sequenceNumber", count.ToString());
                eventMessage.Properties.Add("batchId", BatchId.ToString());
                Console.WriteLine($"\t{DateTime.Now.ToLocalTime()}> Sending message: {count}, Body: [{json}]");

                await moduleClient.SendEventAsync("output1", eventMessage);
                count++;
                await Task.Delay(telemetryInterval);
            }

        }

        static async Task OnDesiredPropertiesUpdated(TwinCollection desiredPropertiesPatch, object userContext)
        {
            // At this point just update the configure configuration.
            if (desiredPropertiesPatch.Contains("TelemetryInterval"))
            {
                telemetryInterval = TimeSpan.FromSeconds((int)desiredPropertiesPatch["TelemetryInterval"]);
            }
            
            var moduleClient = (ModuleClient)userContext;
            var patch = new TwinCollection($"{{ \"TelemetryInterval\": {telemetryInterval.TotalSeconds}}}");
            await moduleClient.UpdateReportedPropertiesAsync(patch); // Just report back last desired property.
        }


    public class SmokerStatus
    {
        [JsonProperty("partitionKey")] public string PartitionKey { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty] public int? ttl { get; set; }
        [JsonProperty] public string SmokerId { get; set; }
        [JsonProperty] public string SessionId { get; set; }
        [JsonProperty] public bool AugerOn { get; set; }
        [JsonProperty] public bool BlowerOn { get; set; }
        [JsonProperty] public bool IgniterOn { get; set; }
        [JsonProperty] public Temps Temps { get; set; }
        [JsonProperty] public bool FireHealthy { get; set; }
        [JsonProperty] public string Mode { get; set; }
        [JsonProperty] public int SetPoint { get; set; }
        [JsonProperty] public DateTime ModeTime { get; set; }
        [JsonProperty] public DateTime CurrentTime { get; set; }
    }
    public class Temps
    {
        public double GrillTemp { get; set; }
        public double Probe1Temp { get; set; }
        public double Probe2Temp { get; set; }
        public double Probe3Temp { get; set; }
        public double Probe4Temp { get; set; }

    }

    }
}