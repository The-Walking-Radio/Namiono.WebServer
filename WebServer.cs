using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Namiono.Common;
using System.Web;

namespace Namiono
{
	public sealed class WebServer<T> : IDisposable
	{
		public delegate void DataReceivedEventHandler(object sender, DataReceivedEventArgs e);
		public delegate void DataSendEventHandler(object sender, DataSendEventArgs e);
		public delegate void ErrorEventHandler(object sender, ErrorEventArgs e);

		public class DataReceivedEventArgs : EventArgs
		{
			public HttpListenerContext Context;
			public RequestType RequestType;
			public RequestTarget Target;
			public SiteAction Action;
			public Provider Provider;
			public HTTPMethod Method;
			public string UserAgent;
			public string Path;
			public Dictionary<string, string> Params;
		}

		public class DataSendEventArgs : EventArgs
		{
			public string Bytessend;
		}

		public class ErrorEventArgs : EventArgs
		{
			public Exception Exception;

			public ErrorEventArgs(Exception ex)
			{
				Exception = ex;
			}
		}

		public event DataReceivedEventHandler DataReceived;

		public event DataSendEventHandler DataSend;
		public event ErrorEventHandler HTTPError;

		HttpListener listener;
		string docroot;

		public WebServer(string path, int port = 80, bool secure = false, string hostname = "*")
		{
			docroot = path;

			listener = new HttpListener();
			listener.Prefixes.Add(string.Format("{0}://{1}:{2}/", 
				secure ? "https" : "http", hostname, port));
		}

		public void Start()
		{
			try
			{
				listener.Start();
				HandleClientConnections(listener).ConfigureAwait(false);
			}
			catch (HttpListenerException ex)
			{
				HTTPError?.Invoke(this, new ErrorEventArgs(ex));
			}
		}

		RequestType GetRequestType(ref HttpListenerContext context, ref Provider provider)
		{
			var type = context.Request.Headers["Request-Type"];
			var reqType = RequestType.sync;

			if (!string.IsNullOrEmpty(type))
				switch (type)
				{
					case "async":
						reqType = RequestType.Async;
						break;
					case "sync":
					default:
						reqType = RequestType.sync;
						break;
				}

			return reqType;
		}

		RequestTarget GetRequestTarget(ref string path, ref HttpListenerContext context)
		{
			var tgt = RequestTarget.Site;

			if (path.StartsWith("/providers/"))
			{
				tgt = RequestTarget.Provider;
			}
			else
			{
				tgt = (path == "/" || path.EndsWith(".html") || path.EndsWith(".cgi") || path.EndsWith(".htm")) ?
					RequestTarget.Site : RequestTarget.File;
			}

			return tgt;
		}

		Provider GetRequestedProvider(ref string path)
		{
			if (path.StartsWith("/providers/") && path.EndsWith("/shoutcast/"))
				return Provider.Shoutcast;

			if (path.StartsWith("/providers/") && path.EndsWith("/sendeplan/"))
				return Provider.Sendeplan;

			if (path.StartsWith("/providers/") && path.EndsWith("/users/"))
				return Provider.User;

			return Provider.None;
		}

		SiteAction GetSiteAction(ref HttpListenerContext context)
		{
			var action = SiteAction.None;

			if (context.Request.Headers["Action"] != null)
				switch (context.Request.Headers["Action"].ToLower())
				{
					case "add":
						Console.WriteLine("Action: Add");
						action = SiteAction.Add;
						break;
					case "edit":

						Console.WriteLine("Action: Edit");
						action = SiteAction.Edit;
						break;
					case "del":

						Console.WriteLine("Action: Delete");
						action = SiteAction.Remove;
						break;
					case "login":

						Console.WriteLine("Action: Login");
						action = SiteAction.Login;
						break;
					case "show":
					default:
						action = SiteAction.None;
						break;
				}

			return action;
		}

		async Task HandleClientConnections(HttpListener listener)
		{
			var context = await listener.GetContextAsync().ConfigureAwait(false);
			var action = GetSiteAction(ref context);

			var method = (context.Request.HttpMethod == "POST") ? HTTPMethod.POST : HTTPMethod.GET;
			var url = context.Request.Url.PathAndQuery.Split('?');
			var path = GetContentType((url[0] == "/") ? "/index.html" : url[0], ref context);
			var formdata = GetPostData((url.Length > 1 && method == HTTPMethod.GET) ?
				string.Format("?{0}", url[1]) : path, ref context, method);

			var target = GetRequestTarget(ref path, ref context);

			var provider = GetRequestedProvider(ref path);
			var reqType = GetRequestType(ref context, ref provider);

			switch (target)
			{
				case RequestTarget.Site:
				case RequestTarget.Provider:
					var directEvArgs = new DataReceivedEventArgs();
					directEvArgs.Action = action;
					directEvArgs.Context = context;
					directEvArgs.Method = method;
					directEvArgs.UserAgent = context.Request.Headers["UAgent"];
					directEvArgs.Params = formdata;
					directEvArgs.Provider = provider;
					directEvArgs.Path = path.Split('?')[0];
					directEvArgs.RequestType = reqType;
					directEvArgs.Target = target;

					DataReceived?.Invoke(this, directEvArgs);
					break;
				case RequestTarget.File:
					var filePath = GetContentType(Filesystem.Combine(docroot, path), ref context);
					var data = SendErrorDocument(404, string.Format("Not Found ({0})!", filePath), ref context);

					using (var fs = new Filesystem(docroot))
					{
						if (fs.Exists(filePath))
						{
							Array.Clear(data, 0, data.Length);
							data = fs.Read(filePath).Result;

							context.Response.StatusCode = 200;
							context.Response.StatusDescription = "OK";
						}
					}

					Send(ref data, ref context);
					break;
				default:
					break;
			}

			await HandleClientConnections(listener).ConfigureAwait(false);
		}

		public byte[] SendErrorDocument(int statusCode, string desc, ref HttpListenerContext context)
		{
			var output = Dispatcher.ReadTemplate(docroot, "static_site");
			output = output.Replace("[#TITLE#]", string.Format("{0}", statusCode));
			output = output.Replace("[#CONTENT#]", string.Format("{0}", desc));

			context.Response.StatusCode = statusCode;
			context.Response.StatusDescription = desc;

			return Encoding.UTF8.GetBytes(output);
		}

		public void Send(ref string data, ref HttpListenerContext context, Encoding encoding)
		{
			var bytes = encoding.GetBytes(data);
			Send(ref bytes, ref context);
		}

		public void Send(ref byte[] data, ref HttpListenerContext context)
		{
			context.Response.Headers.Add("Server", string.Empty);
			context.Response.Headers.Add("Date", string.Empty);

			context.Response.ContentLength64 = data.Length;
			context.Response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
			context.Response.OutputStream.Close();

			DataSend?.Invoke(this, new DataSendEventArgs());
		}

		public void Close()
		{
			if (listener.IsListening)
				listener.Stop();
		}

		public void Dispose() => listener.Close();

		public static Dictionary<string, string> GetPostData(
			string path, ref HttpListenerContext context, HTTPMethod method)
		{
			var formdata = new Dictionary<string, string>();

			switch (method)
			{
				case HTTPMethod.POST:
					var encoding = context.Request.ContentEncoding;
					var ctype = context.Request.ContentType;
					var line = string.Empty;

					using (var reader = new StreamReader(context.Request.InputStream, encoding))
						line = reader.ReadToEnd();

					if (string.IsNullOrEmpty(line))
						return null;

					if (!string.IsNullOrEmpty(ctype))
					{
						if (ctype.Split(';')[0] != "application/x-www-form-urlencoded")
						{
							var boundary = ctype.Split('=')[1];

							if (string.IsNullOrEmpty(line))
								return null;

							var start = line.IndexOf(boundary) + (boundary.Length + 2);
							var end = line.LastIndexOf(boundary) + boundary.Length;
							line = line.Substring(start, end - start);
							var formparts = new List<string>();

							while (line.Contains(boundary))
							{
								if (line.StartsWith("Content-Disposition:"))
								{
									start = line.IndexOf("Content-Disposition: form-data;") +
										"Content-Disposition: form-data;".Length;

									end = line.IndexOf(boundary);
									formparts.Add(line.Substring(start, end - start).TrimStart());
									line = line.Remove(0, end);
								}

								if (line.StartsWith(boundary))
								{
									if (line.Length > boundary.Length + 2)
										line = line.Remove(0, boundary.Length + 2);
									else
										break;
								}
							}

							foreach (var item in formparts)
								if (item.Contains("filename=\""))
								{
									var posttag = item.Substring(0, item.IndexOf(";"));
									var data = item;
									start = data.IndexOf("filename=\"") + "filename=\"".Length;
									data = data.Remove(0, start);
									end = data.IndexOf("\"");

									var filename = data.Substring(0, end);
									if (string.IsNullOrEmpty(filename))
										continue;

									if (filename.Contains("\\") || filename.Contains("/"))
									{
										var parts = filename.Split(filename.Contains("\\") ? '\\' : '/');
										filename = parts[parts.Length - 1];
									}

									start = data.IndexOf("Content-Type: ");
									data = data.Remove(0, start);
									end = data.IndexOf("\r\n");
									var cType = data.Substring(0, end + 2);
									data = data.Remove(0, end + 2);
									var filedata = context.Request.ContentEncoding.GetBytes(data.Substring(2, data.IndexOf("\r\n--")));
									var uploadpath = Filesystem.Combine(path, filename);

									try
									{
										File.WriteAllBytes(uploadpath, filedata);

										if (!formdata.ContainsKey(posttag))
											formdata.Add(posttag, uploadpath);
									}
									catch (Exception ex)
									{
										Console.WriteLine(ex);
										continue;
									}
								}
								else
								{
									var x = item.Replace("\r\n--", string.Empty).Replace("name=\"",
										string.Empty).Replace("\"", string.Empty).Replace("\r\n\r\n", "|").Split('|');
									x[0] = x[0].Replace(" file", string.Empty);

									if (!formdata.ContainsKey(x[0]))
										formdata.Add(x[0], x[1]);
								}

							formparts.Clear();
							formparts = null;
						}
						else
						{
							var tmp = line.Split('&');
							for (var i = 0; i < tmp.Length; i++)
								if (tmp[i].Contains("="))
								{
									var p = tmp[i].Split('=');
									if (!formdata.ContainsKey(p[0]))
										formdata.Add(p[0], HttpUtility.UrlDecode(p[1]).ToString());
								}
						}
					}

					break;
				case HTTPMethod.GET:
					if (path.Contains("?") && path.Contains("&") && path.Contains("="))
					{
						var get_params = HttpUtility.UrlDecode(path).Split('?')[1].Split('&');
						for (var i = 0; i < get_params.Length; i++)
							if (get_params[i].Contains("="))
							{
								var p = get_params[i].Split('=');
								if (!formdata.ContainsKey(p[0]))
									formdata.Add(p[0], p[1]);
							}
					}

					break;
				default:
					break;
			}

			return formdata;
		}

		public static string GetContentType(string path, ref HttpListenerContext context)
		{
			if (path.EndsWith(".css"))
				context.Response.ContentType = "text/css";

			if (path.EndsWith(".js"))
				context.Response.ContentType = "text/javascript";

			if (path.EndsWith(".htm") || path.EndsWith(".html"))
				context.Response.ContentType = "text/html";

			if (path.EndsWith(".png"))
				context.Response.ContentType = "image/png";

			if (path.EndsWith(".jpg") || path.EndsWith(".jpeg"))
				context.Response.ContentType = "image/jpg";

			if (path.EndsWith(".gif"))
				context.Response.ContentType = "image/gif";

			if (path.EndsWith(".appcache"))
				context.Response.ContentType = "text/cache-manifest";

			if (path.EndsWith(".woff2"))
				context.Response.ContentType = "application/font-woff2";

			if (path.EndsWith(".cgi"))
				context.Response.ContentType = "text/html";

			return path.ToLowerInvariant();
		}
	}
}
