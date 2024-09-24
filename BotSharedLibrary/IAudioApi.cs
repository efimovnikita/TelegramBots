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
    Task<JobStatus> CheckTranslationStatus(string jobId);
    
    [Multipart]
    [Post("/api/gateway/audio/v1/transcribe")]
    Task<JobInfo> TranscribeAudio([Header("Authorization")] string authorization,
        [AliasAs("audioFile")] StreamPart file,
        [AliasAs("openaiApiKey")] string openaiApiKey,
        [AliasAs("prompt")] string prompt);

    [Get("/api/gateway/audio/v1/transcribe/status?id={jobId}")]
    Task<JobStatus> CheckTranscriptionStatus(string jobId);
    
    [Multipart]
    [Post("/api/gateway/audio/v1/language")]
    Task<LanguageDetectionResult> DetectLanguageFromAudio([Header("Authorization")] string authorization,
        [AliasAs("audioFile")] StreamPart file);
}