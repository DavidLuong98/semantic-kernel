﻿// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;
using SemanticKernel.Service.Config;
using SemanticKernel.Service.Model;
using SemanticKernel.Service.Storage;

namespace SemanticKernel.Service.Controllers;

[ApiController]
public class BotController : ControllerBase
{
    private readonly ILogger<BotController> _logger;
    private readonly IMemoryStore _memoryStore;
    private readonly ChatSessionRepository _chatRepository;
    private readonly ChatMessageRepository _chatMessageRepository;

    /// <summary>
    /// The constructor of BotController.
    /// </summary>
    /// <param name="memoryStore">The memory store.</param>
    /// <param name="chatRepository">The chat session repository.</param>
    /// <param name="chatMessageRepository">The chat message repository.</param>
    /// <param name="logger">The logger.</param>
    public BotController(
        IMemoryStore memoryStore,
        ChatSessionRepository chatRepository,
        ChatMessageRepository chatMessageRepository,
        ILogger<BotController> logger)
    {
        this._logger = logger;
        this._memoryStore = memoryStore;
        this._chatRepository = chatRepository;
        this._chatMessageRepository = chatMessageRepository;
    }

    /// <summary>
    /// Upload a bot.
    /// </summary>
    /// <param name="kernel">The Semantic Kernel instance.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="bot">The bot object from the message body</param>
    /// <param name="aiServiceOptions">The AI service options.</param>
    /// <param name="botSchemaOptions">The bot schema options.</param>
    /// <returns>The HTTP action result.</returns>
    [Authorize]
    [HttpPost]
    [Route("bot/upload")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UploadAsync(
        [FromServices] IKernel kernel,
        [FromServices] IOptionsSnapshot<AIServiceOptions> aiServiceOptions,
        [FromServices] IOptions<BotSchemaOptions> botSchemaOptions,
        [FromQuery] string userId,
        [FromBody] Bot bot)
    {
        // TODO: We should get userId from server context instead of from request for privacy/security reasons when support multiple users.
        this._logger.LogDebug("Received call to upload a bot");

        if (!this.IsBotCompatible(
                externalBotSchema: bot.Schema,
                externalBotEmbeddingConfig: bot.EmbeddingConfigurations,
                embeddingOptions: aiServiceOptions.Get(AIServiceOptions.EmbeddingPropertyName),
                botSchemaOptions: botSchemaOptions.Value))
        {
            return this.BadRequest("Incompatible schema");
        }

        string chatTitle = $"{bot.ChatTitle} - Clone";
        string chatId = string.Empty;

        // Upload chat history into chat repository and embeddings into memory.
        try
        {
            // 1. Create a new chat and get the chat id.
            var newChat = new ChatSession(userId, chatTitle);
            await this._chatRepository.CreateAsync(newChat);
            chatId = newChat.Id;

            string oldChatId = bot.ChatHistory.First().ChatId;

            // 2. Update the app's chat storage.
            foreach (var message in bot.ChatHistory)
            {
                var chatMessage = new ChatMessage(message.UserId, message.UserName, chatId, message.Content, ChatMessage.AuthorRoles.Participant)
                {
                    Timestamp = message.Timestamp
                };
                await this._chatMessageRepository.CreateAsync(chatMessage);
            }

            // 3. Update the memory.
            await this.BulkUpsertMemoryRecordsAsync(oldChatId, chatId, bot.Embeddings);
        }
        catch
        {
            // TODO: Revert changes if any of the actions failed
            throw;
        }

        return this.Accepted();
    }

    /// <summary>
    /// Download a bot.
    /// </summary>
    /// <param name="kernel">The Semantic Kernel instance.</param>
    /// <param name="chatId">The chat id to be downloaded.</param>
    /// <param name="aiServiceOptions">The AI service options.</param>
    /// <param name="botSchemaOptions">The bot schema options.</param>
    /// <returns>The serialized Bot object of the chat id.</returns>
    [Authorize]
    [HttpGet]
    [Route("bot/download/{chatId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<string>> DownloadAsync(
        [FromServices] IKernel kernel,
        [FromServices] IOptionsSnapshot<AIServiceOptions> aiServiceOptions,
        [FromServices] IOptions<BotSchemaOptions> botSchemaOptions,
        Guid chatId)
    {
        this._logger.LogDebug("Received call to download a bot");
        var memory = await this.CreateBotAsync(
            kernel: kernel,
            chatRepository: this._chatRepository,
            chatMessageRepository: this._chatMessageRepository,
            botSchemaOptions: botSchemaOptions.Value,
            embeddingOptions: aiServiceOptions.Get(AIServiceOptions.EmbeddingPropertyName),
            chatId: chatId);

        return JsonSerializer.Serialize(memory);
    }

    /// <summary>
    /// Check if an external bot file is compatible with the application.
    /// </summary>
    /// <remarks>
    /// If the embeddings are not generated from the same model, the bot file is not compatible.
    /// </remarks>
    /// <param name="externalBotSchema">The external bot schema.</param>
    /// <param name="externalBotEmbeddingConfig">The external bot embedding configuration.</param>
    /// <param name="embeddingOptions">The embedding options.</param>
    /// <param name="botSchemaOptions">The bot schema options.</param>
    /// <returns></returns>
    private bool IsBotCompatible(
        BotSchemaOptions externalBotSchema,
        BotEmbeddingConfig externalBotEmbeddingConfig,
        AIServiceOptions embeddingOptions,
        BotSchemaOptions botSchemaOptions)
    {
        // The app can define what schema/version it supports before the community comes out with an open schema.
        return externalBotSchema.Name.Equals(botSchemaOptions.Name, StringComparison.OrdinalIgnoreCase)
               && externalBotSchema.Version == botSchemaOptions.Version
               && externalBotEmbeddingConfig.AIService == embeddingOptions.AIService
               && externalBotEmbeddingConfig.DeploymentOrModelId.Equals(embeddingOptions.DeploymentOrModelId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Prepare the bot information of a given chat.
    /// </summary>
    /// <param name="kernel">The semantic kernel object.</param>
    /// <param name="chatRepository">The chat session repository object.</param>
    /// <param name="chatMessageRepository">The chat message repository object.</param>
    /// <param name="embeddingOptions">The embedding options.</param>
    /// <param name="botSchemaOptions">The bot schema options.</param>
    /// <param name="chatId">The chat id of the bot</param>
    private async Task<Bot> CreateBotAsync(
        IKernel kernel,
        ChatSessionRepository chatRepository,
        ChatMessageRepository chatMessageRepository, // TODO: unused
        BotSchemaOptions botSchemaOptions,
        AIServiceOptions embeddingOptions,
        Guid chatId)
    {
        var chatIdString = chatId.ToString();
        var bot = new Bot
        {
            // get the bot schema version
            Schema = botSchemaOptions,

            // get the embedding configuration
            EmbeddingConfigurations = new BotEmbeddingConfig
            {
                AIService = embeddingOptions.AIService,
                DeploymentOrModelId = embeddingOptions.DeploymentOrModelId
            }
        };

        // get the chat title
        ChatSession chat = await chatRepository.FindByIdAsync(chatIdString);
        bot.ChatTitle = chat.Title;

        // get the chat history
        bot.ChatHistory = await this.GetAllChatMessagesAsync(chatIdString);

        // get the memory collections associated with this chat
        // TODO: filtering memory collections by name might be fragile.
        var allCollections = (await kernel.Memory.GetCollectionsAsync())
            .Where(collection => collection.StartsWith(chatIdString, StringComparison.OrdinalIgnoreCase));

        foreach (var collection in allCollections)
        {
            List<MemoryQueryResult> collectionMemoryRecords = await kernel.Memory.SearchAsync(
                collection,
                "abc", // dummy query since we don't care about relevance. An empty string will cause exception.
                limit: 999999999, // temp solution to get as much as record as a workaround.
                minRelevanceScore: -1, // no relevance required since the collection only has one entry
                withEmbeddings: true,
                cancellationToken: default
            ).ToListAsync();

            bot.Embeddings.Add(new KeyValuePair<string, List<MemoryQueryResult>>(collection, collectionMemoryRecords));
        }

        return bot;
    }

    /// <summary>
    /// Get chat messages of a given chat id.
    /// </summary>
    /// <param name="chatId">The chat id</param>
    /// <returns>The list of chat messages in descending order of the timestamp</returns>
    private async Task<List<ChatMessage>> GetAllChatMessagesAsync(string chatId)
    {
        // TODO: We might want to set limitation on the number of messages that are pulled from the storage.
        return (await this._chatMessageRepository.FindByChatIdAsync(chatId))
            .OrderByDescending(m => m.Timestamp).ToList();
    }

    /// <summary>
    /// Bulk upsert memory records into memory store.
    /// </summary>
    /// <param name="oldChatId">The original chat id of the memory records.</param>
    /// <param name="chatId">The new chat id that will replace the original chat id.</param>
    /// <param name="embeddings">The list of embeddings of the chat id.</param>
    /// <returns>The function doesn't return anything.</returns>
    private async Task BulkUpsertMemoryRecordsAsync(string oldChatId, string chatId, List<KeyValuePair<string, List<MemoryQueryResult>>> embeddings)
    {
        foreach (var collection in embeddings)
        {
            foreach (var record in collection.Value)
            {
                if (record != null && record.Embedding != null)
                {
                    var newCollectionName = collection.Key.Replace(oldChatId, chatId, StringComparison.OrdinalIgnoreCase);

                    MemoryRecord data = MemoryRecord.LocalRecord(
                        id: record.Metadata.Id,
                        text: record.Metadata.Text,
                        embedding: record.Embedding.Value,
                        description: null, additionalMetadata: null);

                    if (!(await this._memoryStore.DoesCollectionExistAsync(newCollectionName, default)))
                    {
                        await this._memoryStore.CreateCollectionAsync(newCollectionName, default);
                    }

                    await this._memoryStore.UpsertAsync(newCollectionName, data, default);
                }
            }
        }
    }
}
