using MongoDB.Driver;
using Bot.VisualMigraineDiary.Models;
using Microsoft.Extensions.Options;

namespace Bot.VisualMigraineDiary.Services;

public class MigraineEventService
{
    private readonly IMongoCollection<MigraineEvent> _migraineEvents;

    public MigraineEventService(IOptions<MigraineDatabaseSettings> databaseSettings)
    {
        var mongoClient = new MongoClient(databaseSettings.Value.ConnectionString);
        var mongoDatabase = mongoClient.GetDatabase(databaseSettings.Value.DatabaseName);
        _migraineEvents = mongoDatabase.GetCollection<MigraineEvent>(databaseSettings.Value.MigraineEventsCollectionName);
    }

    public async Task<List<MigraineEvent>> GetAsync() =>
        await _migraineEvents.Find(_ => true).ToListAsync();

    public async Task<MigraineEvent?> GetAsync(string id) =>
        await _migraineEvents.Find(x => x.Id == id).FirstOrDefaultAsync();

    public async Task CreateAsync(MigraineEvent newMigraineEvent) =>
        await _migraineEvents.InsertOneAsync(newMigraineEvent);

    public async Task UpdateAsync(string id, MigraineEvent updatedMigraineEvent) =>
        await _migraineEvents.ReplaceOneAsync(x => x.Id == id, updatedMigraineEvent);

    public async Task RemoveAsync(string id) =>
        await _migraineEvents.DeleteOneAsync(x => x.Id == id);

    public async Task<List<MigraineEvent>> GetEventsForDateRangeAsync(DateTime start, DateTime end)
    {
        var filter = Builders<MigraineEvent>.Filter.And(
            Builders<MigraineEvent>.Filter.Gte(e => e.StartTime, start),
            Builders<MigraineEvent>.Filter.Lt(e => e.StartTime, end)
        );
        return await _migraineEvents.Find(filter).ToListAsync();
    }
}