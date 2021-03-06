﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Configuration;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using JIoffe.BIAD.Bots;
using Microsoft.Bot.Builder.AI.QnA;

namespace JIoffe.BIAD.QnABot
{
    public class Startup
    {
        private ILoggerFactory _loggerFactory;
        public IConfiguration Configuration { get; }
        private IHostingEnvironment Environment { get; } 

        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();

            Configuration = builder.Build();
            Environment = env;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            //Instantiate the bot configuration for use in the bot
            //Traffic coming through to your app is protected by the app ID and app secret
            //credentials applied to your Bot Registration on Azure.
            //If you are running locally, these can be blank.
            var secretKey = Configuration.GetSection("botFileSecret")?.Value;
            var botFilePath = Configuration.GetSection("botFilePath")?.Value;

            var botConfig = BotConfiguration.Load(botFilePath ?? @".\BotConfiguration.bot", secretKey);
            if (botConfig == null)
                throw new InvalidOperationException($"The .bot config file could not be loaded from [{botFilePath}]");

            //Step 1) Add the bot configuration as something we can retrieve through DI
            services.AddSingleton(botConfig);

            //Step 2) Configure QnA
            //      - Retrieve the service from botConfig that is of type QnA; 
            //      - (Optional) Throw an exception if the service is null or if any required fields are empty
            var qnaService = botConfig.Services.FirstOrDefault(s => s.Type == ServiceTypes.QnA) as QnAMakerService;
            if (qnaService == null)
                throw new InvalidOperationException($"The QnA Service is not configured correctly in {botFilePath}");

            //Create a new instance of QnAMaker and add it to the services container as a singleton
            var qnaMaker = new QnAMaker(qnaService);
            services.AddSingleton(qnaMaker);

            //The extension to add a bot to our services container
            //can be found in Microsoft.Bot.Builder.Integration.AspNet.Core
            //Whichever type is passed will be the bot that is created
            services.AddBot<QnAMakerBot>(options =>
            {
                //The bot configuration can map different endpoints for different environments
                var serviceName = Environment.EnvironmentName.ToLowerInvariant();
                var service = botConfig.FindServiceByNameOrId(serviceName) as EndpointService;
                if(service == null)
                    throw new InvalidOperationException($"The .bot file does not contain an endpoint with name '{serviceName}'.");

                options.CredentialProvider = new SimpleCredentialProvider(service.AppId, service.AppPassword);


                //Memory storage is only used for this example.
                //In a production application, you should always rely on 
                //a more persistant storage mechanism, such as CosmosDB
                IStorage dataStore = new MemoryStorage();

                //Whichever datastore we're working with, we will need to use it
                //to actually store the conversation state.
                var conversationState = new ConversationState(dataStore);
                options.State.Add(conversationState);

                //Step 3) Add a callback for "OnTurnError" that logs any bot or middleware errors to the console
                //     - (Optional) Send a message to the user indicating there was a problem

                ILogger logger = _loggerFactory.CreateLogger<QnAMakerBot>();
                options.OnTurnError = async (context, e) =>
                {
                    //Ideally you do not want to get here - you want to handle business logic errors more gracefully.
                    //But if we're here, we can do any housekeeping such as logging and letting the know something went wrong.
                    logger.LogError(e, "Unhandled exception on bot turn - incoming input was {0}", context.Activity.Text);

                    //Since we have the context, we can send (helpful) messagse to the user
                    await context.SendActivityAsync($"Something went wrong. Please forgive me. Exception: {e.Message}");
                };
            });
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;

            app.UseDefaultFiles()
                .UseStaticFiles()
                .UseBotFramework();  //Automatically maps endpoint handlers related to bots
        }
    }
}
