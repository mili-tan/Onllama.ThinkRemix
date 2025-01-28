using System.Text;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using ProxyKit;

namespace Onllama.ThinkRemix
{
    public class Program
    {
        public static string TargetApiUrl = "http://127.0.0.1:11434";
        public static string ThinkApiUrl = "http://127.0.0.1:11434";
        public static string ThinkModel = "deepseek-r1:32b";
        public static string[] ThinkSeparator = ["</think>", "**最终答案**", "**Final Answer**"];
        public static OllamaApiClient OllamaApi = new OllamaApiClient(new Uri(ThinkApiUrl));

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

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddAuthorization();
            builder.Services.AddProxy(httpClientBuilder =>
                httpClientBuilder.ConfigureHttpClient(client =>
                    client.Timeout = TimeSpan.FromMinutes(5)));


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

                        Console.WriteLine(jBody.ToString());

                        if (jBody.ContainsKey("messages"))
                        {
                            var msgs = jBody["messages"]?.ToObject<List<Message>>();
                            if (msgs != null && msgs.Any())
                                await foreach (var res in OllamaApi.ChatAsync(new ChatRequest()
                                               {
                                                   Model = thinkModel,
                                                   Messages = msgs.Select(x =>
                                                       new OllamaSharp.Models.Chat.Message(x.Role, x.Content)),
                                                   Stream = false,
                                                   Options = new RequestOptions() {Stop = ThinkSeparator}
                                               }))
                                {
                                    Console.WriteLine(res.Message.Content);
                                    msgs.Add(new Message
                                    {
                                        Role = ChatRole.Assistant.ToString(),
                                        Content = res.Message.Content.Split(ThinkSeparator,
                                                StringSplitOptions.RemoveEmptyEntries).First()
                                            .Replace("<think>", string.Empty).Trim() + Environment.NewLine
                                    });
                                    jBody["messages"] = JArray.FromObject(msgs);
                                    Console.WriteLine(jBody.ToString());
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

    public class Message
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }

        [JsonPropertyName("images")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Images { get; set; }

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? ImageUrl { get; set; }

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? ToolCalls { get; set; }
    }
}
