using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace ParaDesk.Core
{
    internal class UpdateInfo
    {
        public string LatestVersion { get; set; }
        public string Url { get; set; }
        public bool IsNewer { get; set; }
    }

    internal static class UpdateChecker
    {
        public const string ReleasesPage = "https://github.com/sinpoce/ParaDesk/releases";

        internal const string LatestReleaseApi = "https://api.github.com/repos/sinpoce/ParaDesk/releases/latest";

        private const int TimeoutMs = 10000;

        private const int MaxResponseBytes = 1024 * 1024;

        public static void CheckAsync(Action<UpdateInfo, string> done)
        {
            if (done == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = null;
                string error = null;
                try
                {
                    info = FetchLatest(out error);
                }
                catch (Exception ex)
                {
                    Log.Error("检查更新异常", ex);
                    info = null;
                    error = string.Format(L.T("检查更新时出错：{0}"), ex.Message);
                }

                if (info == null && error == null) error = L.T("无法解析 GitHub 的返回内容。");
                if (info != null) error = null;

                try { done(info, error); }
                catch (Exception ex)
                {
                    Log.Error("更新检查回调异常", ex);
                }
            });
        }

        private static UpdateInfo FetchLatest(out string error)
        {
            error = null;

            try
            {
                var sp = ServicePointManager.SecurityProtocol;
                if (sp != SecurityProtocolType.SystemDefault && (sp & SecurityProtocolType.Tls12) == 0)
                {
                    ServicePointManager.SecurityProtocol = sp | SecurityProtocolType.Tls12;
                    Log.Debug("检查更新：进程的 TLS 协议列表不含 TLS 1.2（" + sp + "），已补上");
                }
            }
            catch (Exception ex) { Log.Debug("启用 TLS 1.2 失败: " + ex.Message); }

            var req = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
            req.Method = "GET";
            req.UserAgent = AppInfo.ProductName + "/" + AppInfo.Version;
            req.Accept = "application/vnd.github+json";
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.AllowAutoRedirect = true;

            byte[] body;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    body = ReadBounded(stream, MaxResponseBytes);
                }
            }
            catch (WebException ex)
            {
                error = DescribeWebError(ex);
                Log.Warn("检查更新：请求失败 " + ex.Status + " " + ex.Message);
                return null;
            }

            if (body == null)
            {
                error = L.T("无法解析 GitHub 的返回内容。");
                Log.Warn("检查更新：应答超过 " + MaxResponseBytes + " 字节，已放弃");
                return null;
            }

            var info = ParseRelease(body, AppInfo.Version);
            if (info == null)
            {
                error = L.T("无法解析 GitHub 的返回内容。");
                return null;
            }

            Log.Info("检查更新：最新发布 " + info.LatestVersion + "，当前 " + AppInfo.Version +
                     (info.IsNewer ? "（有新版本）" : ""));
            return info;
        }

        private static string DescribeWebError(WebException ex)
        {
            if (ex.Status == WebExceptionStatus.Timeout)
                return L.T("连接 GitHub 超时。");

            var resp = ex.Response as HttpWebResponse;
            if (ex.Status == WebExceptionStatus.ProtocolError && resp != null)
            {
                int code = (int)resp.StatusCode;
                if (code == 404) return L.T("还没有发布任何版本。");
                if (code == 403 || code == 429)
                    return L.T("GitHub 暂时拒绝了请求（可能是请求过于频繁），请稍后再试。");
                return string.Format(L.T("GitHub 返回了错误：HTTP {0}"), code);
            }

            return string.Format(L.T("无法连接 GitHub：{0}"), ex.Message);
        }

        private static byte[] ReadBounded(Stream stream, int max)
        {
            if (stream == null) return new byte[0];
            using (var ms = new MemoryStream())
            {
                var buf = new byte[16 * 1024];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > max) return null;
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        [DataContract]
        private class ReleaseDto
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
        }

        internal static UpdateInfo ParseRelease(byte[] json, string currentVersion)
        {
            if (json == null || json.Length == 0) return null;
            ReleaseDto dto;
            try
            {
                int offset = 0;
                if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) offset = 3;
                using (var ms = new MemoryStream(json, offset, json.Length - offset))
                    dto = new DataContractJsonSerializer(typeof(ReleaseDto)).ReadObject(ms) as ReleaseDto;
            }
            catch (Exception ex)
            {
                Log.Warn("检查更新：解析应答失败 " + ex.Message);
                return null;
            }
            if (dto == null || string.IsNullOrEmpty(dto.TagName)) return null;

            string latest = StripPrefix(dto.TagName.Trim());
            if (latest.Length == 0) return null;

            return new UpdateInfo
            {
                LatestVersion = latest,
                Url = SafeUrl(dto.HtmlUrl),
                IsNewer = CompareVersions(latest, currentVersion) > 0,
            };
        }

        private static string SafeUrl(string url)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u) &&
                u.Scheme == Uri.UriSchemeHttps &&
                string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return u.AbsoluteUri;
            return ReleasesPage;
        }

        private static string StripPrefix(string v)
        {
            if (v == null) return "";
            v = v.Trim();
            if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
            return v.Trim();
        }

        internal static int CompareVersions(string a, string b)
        {
            string preA, preB;
            List<long> na = ParseCore(a, out preA);
            List<long> nb = ParseCore(b, out preB);

            int len = Math.Max(na.Count, nb.Count);
            for (int i = 0; i < len; i++)
            {
                long x = i < na.Count ? na[i] : 0;
                long y = i < nb.Count ? nb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }

            bool hasA = preA.Length > 0, hasB = preB.Length > 0;
            if (!hasA && !hasB) return 0;
            if (!hasA) return 1;
            if (!hasB) return -1;
            return ComparePrerelease(preA, preB);
        }

        private static List<long> ParseCore(string v, out string prerelease)
        {
            prerelease = "";
            var parts = new List<long>();
            v = StripPrefix(v);

            int plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);
            int dash = v.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = v.Substring(dash + 1).Trim();
                v = v.Substring(0, dash);
            }

            foreach (string seg in v.Split('.'))
                parts.Add(LeadingNumber(seg));

            while (parts.Count > 1 && parts[parts.Count - 1] == 0) parts.RemoveAt(parts.Count - 1);
            return parts;
        }

        private static long LeadingNumber(string s)
        {
            long n = 0;
            bool any = false;
            foreach (char c in (s ?? "").Trim())
            {
                if (c < '0' || c > '9') break;
                any = true;
                n = n * 10 + (c - '0');
                if (n > 1000000000L) break;
            }
            return any ? n : 0;
        }

        private static int ComparePrerelease(string a, string b)
        {
            string[] pa = a.Split('.');
            string[] pb = b.Split('.');
            int len = Math.Min(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                long x, y;
                bool nx = long.TryParse(pa[i], out x);
                bool ny = long.TryParse(pb[i], out y);
                int c;
                if (nx && ny) c = x.CompareTo(y);
                else if (nx) c = -1;
                else if (ny) c = 1;
                else c = string.Compare(pa[i], pb[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c < 0 ? -1 : 1;
            }
            if (pa.Length == pb.Length) return 0;
            return pa.Length < pb.Length ? -1 : 1;
        }
    }
}
