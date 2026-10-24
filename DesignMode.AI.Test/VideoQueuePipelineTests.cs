using DesignMode.AI.Server;
using DesignMode.AI.Server.Services;

namespace DesignMode.AI.Test;

[TestClass]
public class VideoQueuePipelineTests
{
    [TestMethod]
    public void ActivePipeline_UsesCanonicalQueueNames()
    {
        CollectionAssert.AreEqual(
            new[] { "api-requests", "runware-video-polls", "video-jobs" },
            VideoQueuePipeline.QueueNames.ToArray());
    }

    [TestMethod]
    public void FunnelRequest_RoundTrip_PreservesGenerationContext()
    {
        var request = new DesignModeFunctions.OpenAIIdeaRequestQueue
        {
            JobId = "job-1", GenerationJobId = "job-1", ImageId = "image-1",
            ImageUrl = "https://example.test/image.png", Name = "Example",
            Industry = "Testing", ConceptNumber = 2, UserId = "user-1",
            ProjectId = "project-1", FunnelId = "funnel-1"
        };

        var parsed = VideoQueuePipeline.Deserialize<DesignModeFunctions.OpenAIIdeaRequestQueue>(
            VideoQueuePipeline.Serialize(request));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(request.JobId, parsed.JobId);
        Assert.AreEqual(request.GenerationJobId, parsed.GenerationJobId);
        Assert.AreEqual(request.ImageId, parsed.ImageId);
        Assert.AreEqual(request.UserId, parsed.UserId);
        Assert.AreEqual(request.ProjectId, parsed.ProjectId);
        Assert.AreEqual(request.FunnelId, parsed.FunnelId);
    }

    [TestMethod]
    public void PollMessage_RoundTrip_PreservesProviderTaskAndAttempt()
    {
        var message = new DesignModeFunctions.RunwareVideoPollMessage
        { JobId = "job-1", TaskUUID = "provider-task-1", Attempt = 7 };

        var parsed = VideoQueuePipeline.Deserialize<DesignModeFunctions.RunwareVideoPollMessage>(
            VideoQueuePipeline.Serialize(message));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(message.JobId, parsed.JobId);
        Assert.AreEqual(message.TaskUUID, parsed.TaskUUID);
        Assert.AreEqual(message.Attempt, parsed.Attempt);
    }

    [TestMethod]
    public void StorageDescription_DoesNotExposeConnectionDetails()
    {
        Assert.AreEqual("local", VideoQueuePipeline.DescribeStorage("UseDevelopmentStorage=true"));
        Assert.AreEqual("configured", VideoQueuePipeline.DescribeStorage("DefaultEndpointsProtocol=https;AccountName=test"));
    }
}
