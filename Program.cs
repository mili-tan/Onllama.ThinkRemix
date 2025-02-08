using System.ClientModel;
using System.Text;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using OpenAI;
using OpenAI.Chat;
using ProxyKit;

namespace Onllama.ThinkRemix
{
    public class Program
    {
        public static string TargetApiUrl = "http://127.0.0.1:11434";
        public static string ThinkApiUrl = "https://api.deepseek.com";
        public static string ThinkModel = "deepseek-reasoner";
        public static string ThinkApiKey = string.Empty;
        public static string[] ThinkSeparator = ["</think>", "**最终答案**", "**Final Answer**"];
        public static bool UseOllamaStyleThinkApi = false;
        public static int TimeoutMinutes = 15;
        public static bool ShowLog = true;
        public static bool WithThinkToken = true;
        public static int ThinkRound = 1;

        public static void Main(string[] args)
        {
            var configurationRoot = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddJsonFile("appsettings.json")
                .Build();

            TargetApiUrl = configurationRoot["TargetApiUrl"] ?? "http://127.0.0.1:11434";
            ThinkApiUrl = configurationRoot["ThinkApiUrl"] ?? "http://127.0.0.1:11434";
            ThinkModel = configurationRoot["ThinkModel"] ?? "http://127.0.0.1:11434";
            ThinkSeparator = configurationRoot["ThinkSeparator"]?.Split(",") ?? ["</think>", "**最终答案**", "**Final Answer**"];
            ThinkApiKey = configurationRoot["ThinkApiKey"] ?? "";
            ThinkRound = int.Parse(configurationRoot["ThinkRound"] ?? "1");

            TimeoutMinutes = int.Parse(configurationRoot["TimeoutMinutes"] ?? "15");
            UseOllamaStyleThinkApi = configurationRoot["UseOllamaStyleThinkApi"]?.ToLower() == "true";
            ShowLog = configurationRoot["ShowLog"]?.ToLower() == "true";
            WithThinkToken = configurationRoot["WithThinkToken"]?.ToLower() == "true";

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddAuthorization();
            builder.Services.AddProxy(httpClientBuilder =>
                httpClientBuilder.ConfigureHttpClient(client =>
                    client.Timeout = TimeSpan.FromMinutes(TimeoutMinutes)));


            var app = builder.Build();

            // Configure the HTTP request pipeline.

            app.UseAuthorization();

            app.Map("/v1", HandleOpenaiStyleChat);

            app.Run();
        }

        private static void HandleOpenaiStyleChat(IApplicationBuilder svr)
        {
            svr.RunProxy(async context =>
            {
                HttpResponseMessage response;
                try
                {
                    if (context.Request.Method.ToUpper() == "POST")
                    {
                        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                        var jBody = JObject.Parse(body);
                        
                        var thinkModel = ThinkModel;
                        var reqModel = jBody["model"]?.ToString();
                        if (!string.IsNullOrEmpty(reqModel) && reqModel.Contains("+"))
                        {
                            var modelParts = reqModel.Split("+");
                            jBody["model"] = modelParts.FirstOrDefault();
                            thinkModel = modelParts.LastOrDefault();
                        }

                        if (ShowLog) Console.WriteLine(jBody.ToString());

                        if (jBody.ContainsKey("messages"))
                        {
                            var msgs = jBody["messages"]?.ToObject<List<Message>>();

                            if (msgs != null && msgs.Any())
                            {
                                if (UseOllamaStyleThinkApi)
                                {
                                    var resStr = string.Empty;
                                    var oMsgs = msgs.Select(x =>
                                        new OllamaSharp.Models.Chat.Message(x.Role, x.Content)).ToList();

                                    for (int i = 0; i < ThinkRound; i++)
                                    {
                                        if (!string.IsNullOrEmpty(resStr))
                                        {
                                            resStr += (i == ThinkRound - 1 ? "让我最后确认一下…" : "等等…");
                                            oMsgs.Add(new OllamaSharp.Models.Chat.Message(ChatRole.Assistant.ToString(), resStr));
                                            if (ShowLog) Console.WriteLine("RE-Think:");
                                            if (ShowLog) Console.WriteLine(JsonConvert.SerializeObject(oMsgs));
                                        }
                                        await foreach (var res in new OllamaApiClient(ThinkApiUrl).ChatAsync(
                                                           new ChatRequest()
                                                           {
                                                               Model = thinkModel,
                                                               Messages = oMsgs,
                                                               Options = new RequestOptions() { Stop = ThinkSeparator }
                                                           }))
                                        {
                                            Console.Write(res.Message.Content);
                                            resStr += res.Message.Content;
                                        }

                                        Console.WriteLine("Round:" + i);
                                    }

                                    msgs.Add(new Message
                                    {
                                        Role = ChatRole.Assistant.ToString(),
                                        Content = (WithThinkToken ? "<think>" : string.Empty) +
                                                  resStr.Split(ThinkSeparator,
                                                          StringSplitOptions.RemoveEmptyEntries).First()
                                                      .TrimStartString("<think>") +
                                                  (WithThinkToken ? "</think>" : string.Empty) +
                                                  Environment.NewLine
                                    });
                                    jBody["messages"] = JArray.FromObject(msgs);
                                }
                                else
                                {
                                    using var client = new HttpClient();
                                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ThinkApiKey}");
                                    client.Timeout = TimeSpan.FromMinutes(TimeoutMinutes);

                                    var jBodyClone = jBody.DeepClone();
                                    jBodyClone["model"] = thinkModel;
                                    jBodyClone["stream"] = false;

                                    var uriBuilder = new UriBuilder(ThinkApiUrl)
                                    {
                                        Path = "/v1/chat/completions"
                                    };

                                    var result = await client.PostAsync(uriBuilder.Uri.ToString(),
                                        new StringContent(jBodyClone.ToString(), Encoding.UTF8, "application/json"));
                                    result.EnsureSuccessStatusCode();

                                    var responseObject = JObject.Parse(await result.Content.ReadAsStringAsync());
                                    var msgToken = responseObject["choices"]?[0]?["message"];

                                    msgs.Add(new Message
                                    {
                                        Role = ChatRole.Assistant.ToString(),
                                        Content = (WithThinkToken ? "<think>" : string.Empty) +
                                                  (msgToken != null && msgToken.Contains("reasoning_content")
                                                      ? msgToken["reasoning_content"]
                                                      : msgToken?["content"]?.ToString().Split(ThinkSeparator,
                                                              StringSplitOptions.RemoveEmptyEntries).First()
                                                          .TrimStartString("<think>")) +
                                                  (WithThinkToken ? "<think/>" : string.Empty) +
                                                  Environment.NewLine
                                    });

                                    jBody["messages"] = JArray.FromObject(msgs);
                                }

                                if (ShowLog) Console.WriteLine(jBody.ToString());
                            }
                        }

                        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jBody.ToString()));
                        context.Request.ContentLength = context.Request.Body.Length;
                    }

                    response = await context.ForwardTo(new Uri(TargetApiUrl + "/v1")).Send();
                    response.Headers.Add("X-Forwarder-By", "Mondrian.ThinkRemix/0.1");

                    return response;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    response = await context.ForwardTo(new Uri(TargetApiUrl + "/v1")).Send();
                    return response;
                }
            });
        }
    }


    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    public class Message
    {
        [JsonProperty("role")] public string? Role { get; set; }
        [JsonProperty("content")] public string? Content { get; set; }

        [JsonProperty("images")]
        public object? Images { get; set; }

        [JsonProperty("image_url")]
        public object? ImageUrl { get; set; }

        [JsonProperty("tool_calls")]
        public object? ToolCalls { get; set; }
    }

    public static class StringExtensions
    {
        public static string TrimStartString(this string str, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return str;
            return str.Trim().StartsWith(prefix) ? str.Trim().Substring(prefix.Length) : str.Trim();
        }
    }
}
