﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.DataModels.TumblrPosts;
using TumblThree.Applications.DataModels.TumblrSearchJson;
using TumblThree.Applications.Downloader;
using TumblThree.Applications.Parser;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models.Blogs;

namespace TumblThree.Applications.Crawler
{
    [Export(typeof(ICrawler))]
    [ExportMetadata("BlogType", typeof(TumblrSearchBlog))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class TumblrSearchCrawler : AbstractTumblrCrawler, ICrawler
    {
        private readonly IDownloader downloader;
        private string tumblrKey = string.Empty;

        private SemaphoreSlim semaphoreSlim;
        private List<Task> trackedTasks;

        public TumblrSearchCrawler(IShellService shellService, CancellationToken ct, PauseToken pt,
            IProgress<DownloadProgress> progress, ICrawlerService crawlerService, IWebRequestFactory webRequestFactory,
            ISharedCookieService cookieService, IDownloader downloader, ITumblrParser tumblrParser, IImgurParser imgurParser,
            IGfycatParser gfycatParser, IWebmshareParser webmshareParser, IMixtapeParser mixtapeParser, IUguuParser uguuParser,
            ISafeMoeParser safemoeParser, ILoliSafeParser lolisafeParser, ICatBoxParser catboxParser,
            IPostQueue<TumblrPost> postQueue, IBlog blog)
            : base(shellService, crawlerService, ct, pt, progress, webRequestFactory, cookieService, tumblrParser, imgurParser,
                gfycatParser, webmshareParser, mixtapeParser, uguuParser, safemoeParser, lolisafeParser, catboxParser, postQueue,
                blog)
        {
            this.downloader = downloader;
        }

        public async Task CrawlAsync()
        {
            Logger.Verbose("TumblrSearchCrawler.Crawl:Start");

            Task grabber = GetUrlsAsync();
            Task<bool> download = downloader.DownloadBlogAsync();

            await grabber;

            UpdateProgressQueueInformation(Resources.ProgressUniqueDownloads);
            blog.DuplicatePhotos = DetermineDuplicates<PhotoPost>();
            blog.DuplicateVideos = DetermineDuplicates<VideoPost>();
            blog.DuplicateAudios = DetermineDuplicates<AudioPost>();
            blog.TotalCount = (blog.TotalCount - blog.DuplicatePhotos - blog.DuplicateAudios - blog.DuplicateVideos);

            CleanCollectedBlogStatistics();

            await download;

            if (!ct.IsCancellationRequested)
            {
                blog.LastCompleteCrawl = DateTime.Now;
            }

            blog.Save();

            UpdateProgressQueueInformation("");
        }

        private async Task GetUrlsAsync()
        {
            semaphoreSlim = new SemaphoreSlim(shellService.Settings.ConcurrentScans);
            trackedTasks = new List<Task>();
            tumblrKey = await UpdateTumblrKeyAsync("https://www.tumblr.com/search/" + blog.Name);

            GenerateTags();

            foreach (int pageNumber in GetPageNumbers())
            {
                await semaphoreSlim.WaitAsync();

                trackedTasks.Add(CrawlPageAsync(pageNumber));
            }

            await Task.WhenAll(trackedTasks);

            postQueue.CompleteAdding();

            UpdateBlogStats();
        }

        private async Task CrawlPageAsync(int pageNumber)
        {
            try
            {
                string document = await GetSearchPageAsync(pageNumber);
                await AddUrlsToDownloadListAsync(document, pageNumber);
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.Crawling);
            }
            catch
            {
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        private async Task<string> GetSearchPageAsync(int pageNumber)
        {
            if (shellService.Settings.LimitConnectionsApi)
                crawlerService.TimeconstraintApi.Acquire();

            return await RequestPostAsync(pageNumber);
        }

        protected virtual async Task<string> RequestPostAsync(int pageNumber)
        {
            var requestRegistration = new CancellationTokenRegistration();
            try
            {
                string url = "https://www.tumblr.com/search/" + blog.Name + "/post_page/" + pageNumber;
                string referer = @"https://www.tumblr.com/search/" + blog.Name;
                var headers = new Dictionary<string, string> { { "X-tumblr-form-key", tumblrKey }, { "DNT", "1" } };
                HttpWebRequest request = webRequestFactory.CreatePostXhrReqeust(url, referer, headers);
                cookieService.GetUriCookie(request.CookieContainer, new Uri("https://www.tumblr.com/"));

                //Example request body, searching for cars:
                //q=cars&sort=top&post_view=masonry&blogs_before=8&num_blogs_shown=8&num_posts_shown=20&before=24&blog_page=2&safe_mode=true&post_page=2&filter_nsfw=true&filter_post_type=&next_ad_offset=0&ad_placement_id=0&more_posts=true

                string requestBody = "q=" + blog.Name + "&sort=top&post_view=masonry&num_posts_shown=" +
                                     ((pageNumber - 1) * blog.PageSize) + "&before=" + ((pageNumber - 1) * blog.PageSize) +
                                     "&safe_mode=false&post_page=" + pageNumber +
                                     "&filter_nsfw=false&filter_post_type=&next_ad_offset=0&ad_placement_id=0&more_posts=true";
                await webRequestFactory.PerformPostXHRReqeustAsync(request, requestBody);
                requestRegistration = ct.Register(() => request.Abort());
                return await webRequestFactory.ReadReqestToEndAsync(request);
            }
            finally
            {
                requestRegistration.Dispose();
            }
        }

        private async Task AddUrlsToDownloadListAsync(string response, int crawlerNumber)
        {
            while (true)
            {
                if (CheckIfShouldStop())
                    return;

                CheckIfShouldPause();

                var result = ConvertJsonToClass<TumblrSearchJson>(response);
                if (string.IsNullOrEmpty(result.Response.PostsHtml))
                {
                    return;
                }

                try
                {
                    string html = result.Response.PostsHtml;
                    html = Regex.Unescape(html);
                    AddPhotoUrlToDownloadList(html);
                    AddVideoUrlToDownloadList(html);
                }
                catch (NullReferenceException)
                {
                }

                if (!string.IsNullOrEmpty(blog.DownloadPages))
                    return;

                Interlocked.Increment(ref numberOfPagesCrawled);
                UpdateProgressQueueInformation(Resources.ProgressGetUrlShort, numberOfPagesCrawled);
                response = await GetSearchPageAsync(crawlerNumber + shellService.Settings.ConcurrentScans);
                crawlerNumber += shellService.Settings.ConcurrentScans;
            }
        }

        private void AddPhotoUrlToDownloadList(string document)
        {
            if (!blog.DownloadPhoto)
                return;
            AddTumblrPhotoUrl(document);

            if (blog.RegExPhotos)
                AddGenericPhotoUrl(document);
        }

        private void AddVideoUrlToDownloadList(string document)
        {
            if (!blog.DownloadVideo)
                return;
            AddTumblrVideoUrl(document);
            AddInlineTumblrVideoUrl(document, tumblrParser.GetTumblrVVideoUrlRegex());

            if (blog.RegExVideos)
                AddGenericVideoUrl(document);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                semaphoreSlim?.Dispose();
                downloader.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
