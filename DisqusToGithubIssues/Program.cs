using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using System.Xml;

namespace DisqusToGithubIssues
{
    internal class Program
    {
        private static readonly HttpClient client = new HttpClient();

        private static async Task Main(string[] args)
        {
            if (args.Length != 4)
            {
                Console.WriteLine("You must supply 4 command-line arguments: <path to Disqus XML file>, <GitHub username>, <GitHub repo name>, <GitHub personal access token>");
                return;
            }

            client.DefaultRequestHeaders.Add("User-Agent", ".NET Disqus to GitHub Issue Importer");

            var path = args[0];
            string repoOwner = args[1];
            string repoName = args[2];
            string PAT = args[3];

            if (File.Exists(path))
            {
                Console.WriteLine($"File '{path}' found.");

                var doc = new XmlDocument();
                doc.Load(path);
                var nsmgr = new XmlNamespaceManager(doc.NameTable);
                nsmgr.AddNamespace(String.Empty, "http://disqus.com");
                nsmgr.AddNamespace("def", "http://disqus.com");
                nsmgr.AddNamespace("dsq", "http://disqus.com/disqus-internals");

                IEnumerable<Post> posts = FindPosts(doc, nsmgr);
                IEnumerable<Thread> threads = await FindThreads(doc, nsmgr, posts);

                Console.WriteLine($"{threads.Count()} valid threads found");
                Console.WriteLine($"{posts.Count()} valid posts found");

                PrepareThreads(threads, posts);

                await PostIssuesToGitHub(threads, repoOwner, repoName, PAT);

            }
            else
            {
                Console.WriteLine($"File '{path}' not found.");
            }

            Console.ReadKey();
        }

        private static async Task<IEnumerable<Thread>> FindThreads(XmlDocument doc, XmlNamespaceManager nsmgr, IEnumerable<Post> posts)
        {
            var xthreads = doc.DocumentElement.SelectNodes("def:thread", nsmgr);

            var threads = new List<Thread>();
            var i = 0;
            foreach (XmlNode xthread in xthreads)
            {
                i++;

                long threadId = xthread.AttributeValue<long>(0);
                var isDeleted = xthread["isDeleted"].NodeValue<bool>();
                var isClosed = xthread["isClosed"].NodeValue<bool>();
                var url = xthread["link"].NodeValue();
                var isValid = await CheckThreadUrl(url, threadId, posts);

                Console.WriteLine($"{i:###} Found thread ({threadId}) '{xthread["title"].NodeValue()}'");

                if (isDeleted)
                {
                    Console.WriteLine($"{i:###} Thread ({threadId}) was deleted.");
                    continue;
                }
                if (isClosed)
                {
                    Console.WriteLine($"{i:###} Thread ({threadId}) was closed.");
                    continue;
                }
                if (!isValid)
                {
                    Console.WriteLine($"{i:###} the url Thread ({threadId}) is not valid: {url}");
                    continue;
                }

                Console.WriteLine($"{i:###} Thread ({threadId}) is valid");
                threads.Add(new Thread(threadId)
                {
                    Title = HttpUtility.HtmlDecode(xthread["title"].NodeValue()),
                    Url = url,
                    CreatedAt = xthread["createdAt"].NodeValue<DateTime>()

                });
            }

            return threads;
        }

        private static async Task<bool> CheckThreadUrl(string url, long threadId, IEnumerable<Post> posts)
        {
            if (!url.StartsWith("http://www.damirscorner.com") &&
               !url.StartsWith("https://www.damirscorner.com"))
            {
                return false;
            }

            if (!posts.Any(p => p.ThreadId == threadId))
            {
                return false;
            }

            try
            {
                await Task.Delay(100);
                var response = await client.GetAsync(url);

                var valid = (response.StatusCode == HttpStatusCode.OK);

                if (!valid)
                {
                    Console.WriteLine($"url {url} not valid because http response status is {response.StatusCode}");
                }

                return valid;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                return false;
            }
        }

        private static IEnumerable<Post> FindPosts(XmlDocument doc, XmlNamespaceManager nsmgr)
        {
            var xposts = doc.DocumentElement.SelectNodes("def:post", nsmgr);
            var posts = new List<Post>();
            var i = 0;
            foreach (XmlNode xpost in xposts)
            {
                i++;
                long postId = xpost.AttributeValue<long>(0);
                Console.WriteLine($"{i:###} found Post ({postId}) by {xpost["author"].FirstChild.NodeValue()}");

                var isDeleted = xpost["isDeleted"].NodeValue<bool>();
                var isSpam = xpost["isSpam"].NodeValue<bool>();

                if (isDeleted)
                {
                    Console.WriteLine($"{i:###} post ({postId}) was deleted");
                    continue;
                }
                if (isDeleted)
                {
                    Console.WriteLine($"{i:###} post ({postId}) was marked as spam");
                    continue;
                }


                Console.WriteLine($"{i:###} post ({postId}) is valid");
                var post = new Post(postId)
                {
                    ThreadId = xpost["thread"].AttributeValue<long>(0),
                    Parent = xpost["parent"].AttributeValue<long>(0),
                    Message = xpost["message"].NodeValue(),
                    CreatedAt = xpost["createdAt"].NodeValue<DateTime>(),
                    Author = xpost["author"].FirstChild.NodeValue()

                };
                posts.Add(post);
            }

            return posts;
        }

        private static void PrepareThreads(IEnumerable<Thread> threads, IEnumerable<Post> posts)
        {
            foreach (var thread in threads)
            {
                var threadsPosts = posts
                    .Where(x => x.ThreadId == thread.Id)
                    .OrderBy(x => x.CreatedAt);
                thread.Posts.AddRange(threadsPosts);

                Console.WriteLine($"Thread ({thread.Id}) '{thread.Title}' has {thread.Posts.Count} posts");
            }
        }

        private static async Task PostIssuesToGitHub(IEnumerable<Thread> threads, string repoOwner, string repoName, string PAT)
        {
            var client = new GitHubClient(new ProductHeaderValue("DisqusToGithubIssues"));
            var basicAuth = new Credentials(PAT);
            client.Credentials = basicAuth;

            var issues = await client.Issue.GetAllForRepository(repoOwner, repoName);
            foreach (var thread in threads)
            {
                var issueTitle = new Uri(thread.Url).AbsolutePath;

                if (thread.Posts.Count == 0)
                {
                    continue;
                }

                if (issues.Any(x => !x.ClosedAt.HasValue && x.Title.Equals(issueTitle)))
                {
                    continue;
                }

                var newIssue = new NewIssue(issueTitle);
                newIssue.Body = $@"Imported 

URL: {thread.Url}
";

                var issue = await client.Issue.Create(repoOwner, repoName, newIssue);
                Console.WriteLine($"New issue (#{issue.Number}) created: {issue.Url}");
                await Task.Delay(1000 * 5);

                foreach (var post in thread.Posts)
                {
                    var message = $@"Imported comment written by **{post.Author}** on **{post.CreatedAt.ToString("s")}**

{post.Message}
";

                    var comment = await client.Issue.Comment.Create(repoOwner, repoName, issue.Number, message);
                    Console.WriteLine($"New comment by {post.Author} at {post.CreatedAt.ToString("s")}");
                    await Task.Delay(1000 * 5);
                }
            }
        }
    }
}
