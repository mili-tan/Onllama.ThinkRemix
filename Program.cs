using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using ProxyKit;

namespace Onllama.ThinkRemix
{
    public class Program
    {
        public static string TargetApiUrl = "http://127.0.0.1:11434";
        public static string ActionApiUrl = "http://127.0.0.1:11434";
        public static string ThinkModel = "deepseek-r1:1.5b";
        public static string[] ThinkSeparator = ["</think>", "**×îÖÕ´ð°¸**", "**Final Answer**"];
        public static OllamaApiClient OllamaApi = new OllamaApiClient(new Uri(ActionApiUrl));

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddAuthorization();


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


                        Console.WriteLine(jBody.ToString());

                        if (jBody.ContainsKey("messages"))
                        {
                            var msgs = jBody["messages"]?.ToObject<List<Message>>();
                            if (msgs != null && msgs.Any())
                            {

                                await foreach (var res in OllamaApi.ChatAsync(new ChatRequest()
                                               {
                                                   Model = ThinkModel,
                                                   Messages = msgs.Select(x =>
                                                       new OllamaSharp.Models.Chat.Message(x.Role, x.Content)),
                                                   Stream = false
                                               }))
                                {
                                    msgs.Add(new Message
                                    {
                                        Role = ChatRole.Assistant.ToString(),
                                        Content = res.Message.Content.Split(ThinkSeparator,
                                            StringSplitOptions.RemoveEmptyEntries).First().Replace("<think>", string.Empty)
                                    });
                                    jBody["messages"] = JArray.FromObject(msgs);
                                    Console.WriteLine(jBody.ToString());
                                }
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
