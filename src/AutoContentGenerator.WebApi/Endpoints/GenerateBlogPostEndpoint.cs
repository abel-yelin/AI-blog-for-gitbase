using AutoContentGenerator.WebApi.Models;
using AutoContentGenerator.WebApi.Services;
using LibGit2Sharp;
using Newtonsoft.Json.Linq;
using Octokit;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;
using System.IO;
using FileMode = System.IO.FileMode;

using Repository = LibGit2Sharp.Repository;

namespace AutoContentGenerator.WebApi.Endpoints;

public class GenerateBlogPostEndpoint
{
    private readonly OpenAIService _openAIService;

    public GenerateBlogPostEndpoint(OpenAIService openAIService)
    {
        _openAIService = openAIService;
    }

    public async Task<IResult> GenerateBlogPost(AppConfig appConfig)
    {
        // 测试 OpenAI API 调用
        string testResponse = await _openAIService.SendChatRequest("Generate a short blog post title about AI.");
        Console.WriteLine($"OpenAI API Test Response: {testResponse}");

        // 测试 OpenAI API 连接
        bool isConnected = await _openAIService.TestConnection();
        if (!isConnected)
        {
            return TypedResults.BadRequest("Failed to connect to OpenAI API.");
        }

        if (appConfig?.GitHubConfig == null || appConfig.OpenAIConfig == null)
        {
            return TypedResults.BadRequest("GitHub or OpenAI configuration is missing.");
        }

        if (appConfig.GitHubConfig.GitHubToken.StartsWith("<<") || appConfig.OpenAIConfig.OpenAIApiKey.StartsWith("<<"))
        {
            return TypedResults.BadRequest("GitHub or OpenAI configuration is missing.");
        }

        // Read GitHub configuration from environment variables
        string gitHubToken = appConfig.GitHubConfig.GitHubToken;
        string repoOwner = appConfig.GitHubConfig.GitHubRepoOwner;
        string repoName = appConfig.GitHubConfig.GitHubRepoName;
        string gitHubUser = appConfig.GitHubConfig.GitHubEmail;
        string postsDirectory = appConfig.GitHubConfig.GitHubPostsDirectory;

        // Initialize GitHub client
        var gitHubClient = new GitHubClient(new ProductHeaderValue("AutoContentGenerator"))
        {
            Credentials = new Octokit.Credentials(repoOwner, gitHubToken)
        };

        // Clone the repository
        string repoUrl = $"https://github.com/{repoOwner}/{repoName}.git";
        string localPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        Console.WriteLine($"Cloning repository from {repoUrl} to {localPath}");

        var libgit2sharpCredentials = new UsernamePasswordCredentials
        {
            Username = appConfig.GitHubConfig.GitHubToken,
            Password = string.Empty
        };

        var cloneOptions = new CloneOptions
        {
            CredentialsProvider = (_url, _user, _cred) => libgit2sharpCredentials,
            FetchOptions = new FetchOptions
            {
                CustomHeaders = new string[] { $"Authorization: token {appConfig.GitHubConfig.GitHubToken}" }
            }
        };

        try
        {
            if (Directory.Exists(localPath))
            {
                Directory.Delete(localPath, true);
            }
            Directory.CreateDirectory(localPath);

            string tempRepoPath = await CloneRepositoryWithRetry(repoUrl, localPath, cloneOptions);
            Console.WriteLine("Clone successful");
        }
        catch (LibGit2SharpException ex)
        {
            Console.WriteLine($"Clone failed: {ex.Message}");
            Console.WriteLine($"Detailed error: {ex}");
            throw;
        }

        string postsPath = Path.Combine(localPath, postsDirectory);
        Console.WriteLine($"Searching for Markdown files in {postsPath}");

        if (!Directory.Exists(postsPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {postsPath}");
        }

        var files = Directory.GetFiles(postsPath, "*.md", SearchOption.AllDirectories);
        Console.WriteLine($"Found {files.Length} Markdown files");

        string[] filePaths = files.Select(Path.GetFileName).ToArray();
        string[] fileNames = filePaths.Select(Path.GetFileNameWithoutExtension).ToArray();
        string filesList = string.Join("\n", fileNames);

        // Generate the new Markdown file
        var blogPost = await WriteBlogPost(filesList);

        string markdownContent = blogPost.Content;
        string newFilePath = Path.Combine(localPath, postsDirectory, blogPost.Title + ".md");
        await File.WriteAllTextAsync(newFilePath, markdownContent);

        // Define newBranchName
        string newBranchName = $"blog-post-{DateTime.UtcNow:yyyyMMddHHmmss}";

        // Commit the new file
        using (var repo = new Repository(localPath))
        {
            // Create a new branch
            var branch = repo.CreateBranch(newBranchName);
            Commands.Checkout(repo, branch);

            // Add the new file and commit
            Commands.Stage(repo, newFilePath);
            
            // Check if there are changes to commit
            var status = repo.RetrieveStatus(new StatusOptions());
            if (status.IsDirty)
            {
                var author = new LibGit2Sharp.Signature("GPT-Blog-Writer", gitHubUser, DateTimeOffset.Now);
                repo.Commit("Add new blog post", author, author);

                // Push the new branch
                var remote = repo.Network.Remotes["origin"];
                var options = new PushOptions { CredentialsProvider = (_, __, ___) => libgit2sharpCredentials };
                repo.Network.Push(remote, $"refs/heads/{newBranchName}", options);
            }
            else
            {
                Console.WriteLine("No changes detected. Skipping commit and push.");
                return TypedResults.BadRequest("No changes detected. Unable to create a new blog post.");
            }
        }

        // Create a pull request
        try
        {
            var pr = new NewPullRequest($"Add new blog post {blogPost.Title}", newBranchName, "main");
            var createdPr = await gitHubClient.PullRequest.Create(repoOwner, repoName, pr);

            return TypedResults.Ok(createdPr.HtmlUrl);
        }
        catch (ApiValidationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private async Task<BlogPost> WriteBlogPost(string existingPosts)
    {
        string prompt = $@"作为一个专业的博客作者，请根据以下信息创作一篇新的博客文章：

1. 现有的博客文章标题列表：
{existingPosts}

2. 要求：
   - 创作一篇与现有文章主题不重复的新文章
   - 文章应该是关于美甲、指甲装饰或指甲的美化的话题
   - 文章长度应该在800到1200字之间
   - 使用markdown格式
   - 包含一个引人入胜的标题
   - 分成几个小节，每个小节都有小标题
   - 在文章末尾添加一个简短的总结

请首先给出文章标题，然后空一行，再给出文章正文内容。";

        string content = await _openAIService.SendChatRequest(prompt);
        
        // Assuming content contains title and body, we need to parse it
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string title = lines[0].Trim();
        string body = string.Join("\n", lines.Skip(1)).Trim();

        return new BlogPost
        {
            Title = title,
            Content = body
        };
    }

    private async Task<string> CloneRepositoryWithRetry(string repoUrl, string tempRepoPath, CloneOptions cloneOptions, int maxRetries = 5)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                Console.WriteLine($"尝试克隆仓库 (第 {i + 1} 次尝试): {repoUrl} 到 {tempRepoPath}");
                Repository.Clone(repoUrl, tempRepoPath, cloneOptions);
                Console.WriteLine("仓库克隆成功");
                return tempRepoPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"克隆尝试 {i + 1} 失败: {ex.Message}");
                Console.WriteLine($"异常详情: {ex}");
                if (i == maxRetries - 1)
                {
                    throw new Exception($"多次尝试后仍无法克隆仓库 {repoUrl}", ex);
                }
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        throw new Exception("Failed to clone repository after multiple attempts");
    }

    public static string ToKebabCase(string title)
    {
        var words = Regex.Split(title, @"\s+")
            .Select(word => Regex.Replace(word.ToLowerInvariant(), @"[^a-z-]", ""));
        return string.Join("-", words.Where(word => !string.IsNullOrWhiteSpace(word)));
    }

    private async Task<List<string>> GetExistingPosts(AppConfig appConfig)
    {
        var github = new GitHubClient(new ProductHeaderValue("YourAppName"))
        {
            Credentials = new Octokit.Credentials(appConfig.GitHubConfig.GitHubToken)
        };

        var repository = await github.Repository.Get(appConfig.GitHubConfig.GitHubRepoOwner, appConfig.GitHubConfig.GitHubRepoName);
        var contents = await github.Repository.Content.GetAllContents(repository.Id, appConfig.GitHubConfig.GitHubPostsDirectory);

        return contents.Select(content => content.Content).ToList();
    }
}

public class QuotingEventEmitter : ChainedEventEmitter
{
    public QuotingEventEmitter(IEventEmitter nextEmitter) : base(nextEmitter)
    {
    }

    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        eventInfo.Style = ScalarStyle.DoubleQuoted;
        base.Emit(eventInfo, emitter);
    }
}