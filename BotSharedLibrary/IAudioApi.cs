using Refit;

namespace BotSharedLibrary;

public interface IAudioApi
{
    [Get("/api/gateway/audio/v1/health")]
    Task CheckHealth();
    
    [Multipart]
    [Post("/api/gateway/audio/v1/translate/to-english")]
    Task<JobInfo> MakeTranslationFromAudio([Header("Authorization")] string authorization,
        [AliasAs("audioFile")] StreamPart file,
        [AliasAs("prompt")] string prompt,
        [AliasAs("openaiApiKey")] string openaiApiKey);
    
    [Get("/api/gateway/audio/v1/translate/status?id={jobId}")]
    Task<JobStatus> CheckJobStatus(string jobId);
    
    [Multipart]
    [Post("/api/gateway/audio/v1/language")]
    Task<LanguageDetectionResult> DetectLanguageFromAudio([Header("Authorization")] string authorization,
        [AliasAs("audioFile")] StreamPart file);
}