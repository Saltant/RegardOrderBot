using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RegardOrderBot.Extensions;
using RegardOrderBot.POCO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Net.Mail;
using System.IO;
using System.Linq;

namespace RegardOrderBot
{
	public class OrderBot : IHostedService
	{
		readonly ILogger<OrderBot> logger;
		readonly IHostApplicationLifetime hostAppLifetime;
		readonly IConfiguration configuration;
		readonly List<Product> products = new List<Product>();
		readonly bool isHaveCommandLineArgs;
		readonly IServiceProvider serviceProvider;
		readonly public static CultureInfo culture = CultureInfo.CreateSpecificCulture("ru-RU");
		readonly string userName;
		readonly string userPhoneNumber;
		const string SHOP_DOMAIN = "https://www.regard.ru";
		RegardParser regardParser;
		int artNumber;
		int maxPrice;
		public List<Product> Products
		{
			get
			{
				return products;
			}
		}
		public OrderBot(ILogger<OrderBot> logger, IHostApplicationLifetime hostAppLifetime, IConfiguration configuration, IServiceProvider serviceProvider)
		{
			this.logger = logger;
			this.hostAppLifetime = hostAppLifetime;
			this.configuration = configuration;
			this.serviceProvider = serviceProvider;
			culture.NumberFormat.CurrencySymbol = "руб.";
			culture.NumberFormat.NumberDecimalSeparator = ".";
			userName = configuration.GetSection("Credentials")["UserName"];
			userPhoneNumber = configuration.GetSection("Credentials")["UserPhoneNumber"];
			if (ValidatePhoneNumber(userPhoneNumber, out string validatedPhoneNumber))
				userPhoneNumber = validatedPhoneNumber;

			try
			{
				isHaveCommandLineArgs = ReadCommandLineArgs(configuration);
			}
			catch(Exception ex)
			{
				logger.LogError(ex.Message);
			}
			finally
			{
				if (!isHaveCommandLineArgs)
					configuration.GetSection("Products").Bind(products);
			}
		}

		static bool ValidatePhoneNumber(string userPhoneNumber, out string validatedPhoneNumber)
		{
			bool result = false;
			validatedPhoneNumber = string.Empty;
			char[] symbols = userPhoneNumber.Where(x => char.IsDigit(x)).ToArray();
			if(symbols.Length <= 12)
			{
				validatedPhoneNumber = new string(symbols);
				result = true;
			}
			return result;
		}

		bool ReadCommandLineArgs(IConfiguration commandLineArgs)
		{
			if(commandLineArgs["art"] == null || commandLineArgs["maxprice"] == null)
				return false;

			artNumber = commandLineArgs["art"].ToInt();
			maxPrice = commandLineArgs["maxprice"].ToInt();
			products.Add(new Product { ArtNumber = artNumber, MaxPrice = maxPrice });
			return true;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			if(string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(userPhoneNumber))
			{
				logger.LogError("Ошибка! не заполнены настройки имени пользователя и/или номера телефона (appsettings.json)");
				hostAppLifetime.StopApplication();
			}

			regardParser = serviceProvider.GetService<RegardParser>();
			bool? isParserStarted = regardParser?.Start();
			if (isParserStarted == null)
			{
				logger.LogError($"Ошибка! не удалось получить сервис {nameof(RegardParser)}");
				hostAppLifetime.StopApplication();
			}else if (isParserStarted == true)
			{
				logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Парсер товаров успешно запущен.");
			}else if(isParserStarted == false)
			{
				hostAppLifetime.StopApplication();
			}

			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			logger.LogInformation("Приложение остановлено.");
			return Task.CompletedTask;
		}

		internal async Task<OrderResult> OrderProduct(Product product, string productToken, string phpsessid)
		{
			logger.LogInformation($"Попытка заказа товара: [{product.ArtNumber}] {product.ProductName} - текущая цена: {product.CurrentPrice.ToString("C", culture)}");
			OrderResult orderResult = new OrderResult
			{
				TrackedStatus = RegardParser.TrackedStatus.InOrderProcess,
				Content = string.Empty
			};
			var cert = await GetServerCertificateAsync(SHOP_DOMAIN);
			var cookieContainer = new CookieContainer();
			var handler = new HttpClientHandler
			{
				CookieContainer = cookieContainer
			};
			handler.ClientCertificates.Add(cert);
			using (HttpClient client = new HttpClient(handler))
			{
				client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0.4324.104 Safari/537.36");
				client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
				client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
				client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
				client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
				{
					NoStore = true,
					NoCache = true,
					MustRevalidate = true
				};
				client.DefaultRequestHeaders.Referrer = new Uri($"{RegardParser.PRODUCT_LINK}{product.ArtNumber}.htm");
				cookieContainer.Add(new Uri(SHOP_DOMAIN), new Cookie("PHPSESSID", phpsessid));

				var message = new HttpRequestMessage(HttpMethod.Get, $"https://www.regard.ru/ajax/quick_order_small.php?good_id={product.ArtNumber}&type=1&fam={userName}&tel={userPhoneNumber}&token={productToken}&tokenName=quick_order&close_button=false");
				message.Headers.Add("Cookie", $"PHPSESSID={phpsessid}");

				HttpResponseMessage response = await client.SendAsync(message);
				if (!response.IsSuccessStatusCode)
					orderResult.TrackedStatus = RegardParser.TrackedStatus.FailOrderProcess;
				else
				{
					var content = await response.Content.ReadAsStringAsync();
					orderResult.Content = content;
					orderResult.TrackedStatus = RegardParser.TrackedStatus.ProductOrdered;
				}
			}
			return await Task.FromResult(orderResult);
		}

		internal async Task<bool> ProductPriceLargerMaxPrice(Product product, DateTime maxPriceCheckTimestamp, bool isFirstAttempt)
		{
			bool result = false;
			bool isSendEmail = Convert.ToBoolean(configuration.GetSection("Configuration").GetSection("Notification")["SendEmailIfProductPriceLargerMaxPrice"]);
			if (isSendEmail & DateTime.Now.Subtract(maxPriceCheckTimestamp).TotalHours >= 1 || isFirstAttempt)
			{
				if(SendEmail(product, out Exception exResult))
				{
					logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] товар [{product.ArtNumber}] {product.ProductName} " +
						$"в наличии но его цена {product.CurrentPrice.ToString("C", culture)} руб. " +
						$"[заданная максимальная цена товара: {product.MaxPrice} руб.]");
					result = true;
				}
				if (exResult != null)
					logger.LogError(exResult.Message);
			}
			return await Task.FromResult(result);
		}

		static async Task<X509Certificate2> GetServerCertificateAsync(string url)
		{
			X509Certificate2 certificate = null;
			var httpClientHandler = new HttpClientHandler
			{
				ServerCertificateCustomValidationCallback = (_, cert, __, ___) =>
				{
					certificate = new X509Certificate2(cert.GetRawCertData());
					return true;
				}
			};

			var httpClient = new HttpClient(httpClientHandler);
			await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));

			return certificate ?? throw new NullReferenceException();
		}

		internal void ProductSuccessfulOrdered(Product product, string orderNumber)
		{
			logger.LogInformation($"Товар: [{product.ArtNumber}] {product.ProductName} - Успешно заказан! [Номер заказа: {orderNumber}]");
			if (SendEmail(product, orderNumber, out Exception exResult))
				logger.LogInformation($"Email о заказе №{orderNumber} успешно отправлен.");
			if (exResult != null)
				logger.LogError(exResult.Message);
			regardParser.TrackingProducts[product.ArtNumber].Cancel();
		}

		bool SendEmail(Product product, string orderNumber, out Exception exResult)
		{
			bool isSame = Convert.ToBoolean(configuration.GetSection("Configuration").GetSection("Notification")["ClientEmailIsSameAsServerEmail"]);
			string serverEmail = configuration.GetSection("Configuration").GetSection("ServerEmail")["Email"];
			exResult = null;
			bool result = false;
			MailAddress from = new MailAddress(serverEmail, configuration.GetSection("Configuration").GetSection("ServerEmail")["EmailDisplay"]);
			MailAddress to;
			if (isSame)
				to = new MailAddress(serverEmail);
			else
				to = new MailAddress(configuration.GetSection("Configuration").GetSection("ClientEmail")["Email"]);

			string logo = Path.Combine(AppContext.BaseDirectory, "Images/email-logo.png");
			string warning = Path.Combine(AppContext.BaseDirectory, "Images/email-warning.png");

			string rawBody = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "OrderEmailNotification.html"));
			string body = rawBody
				.Replace("cid:product@Link", $"{RegardParser.PRODUCT_LINK}{product.ArtNumber}htm")
				.Replace("cid:product@Name", product.ProductName)
				.Replace("cid:product@CurrentPrice", product.CurrentPrice.ToString("C", culture))
				.Replace("cid:product@MaxPrice", product.MaxPrice.ToString("C", culture))
				.Replace("cid:order@Number", orderNumber);

			MailMessage m = new MailMessage(from, to)
			{
				Subject = "Заказ товара",
				Body = body,
				IsBodyHtml = true
			};
			m.Attachments.Add(new Attachment(logo) { ContentId = "email-logo@png" });
			m.Attachments.Add(new Attachment(warning) { ContentId = "email-warning@png" });

			SmtpClient smtp = new SmtpClient(configuration.GetSection("Configuration").GetSection("ServerEmail")["EmailSmtpHost"], int.Parse(configuration.GetSection("Configuration").GetSection("ServerEmail")["EmailSmtpPort"]))
			{
				Credentials = new NetworkCredential(serverEmail, configuration.GetSection("Configuration").GetSection("ServerEmail")["EmailPassword"]),
				EnableSsl = true
			};
			try
			{
				smtp.Send(m);
				result = true;
			}
			catch (Exception ex)
			{
				exResult = ex;
			}
			return result;
		}
		bool SendEmail(Product product, out Exception exResult)
		{
			bool isSame = Convert.ToBoolean(configuration.GetSection("Configuration").GetSection("Notification")["ClientEmailIsSameAsServerEmail"]);
			string serverEmail = configuration.GetSection("Configuration").GetSection("ServerEmail")["Email"];
			exResult = null;
			bool result = false;
			MailAddress from = new MailAddress(serverEmail, configuration.GetSection("Configuration").GetSection("ServerEmail")["EmailDisplay"]);
			MailAddress to;
			if (isSame)
				to = new MailAddress(serverEmail);
			else
				to = new MailAddress(configuration.GetSection("Configuration").GetSection("ClientEmail")["Email"]);

			string logo = Path.Combine(AppContext.BaseDirectory, "Images/email-logo.png");
			string warning = Path.Combine(AppContext.BaseDirectory, "Images/email-warning.png");

			string rawBody = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PriceCheckEmailNotification.html"));
			string body = rawBody
				.Replace("cid:product@Link", $"{RegardParser.PRODUCT_LINK}{product.ArtNumber}htm")
				.Replace("cid:product@Name", product.ProductName)
				.Replace("cid:product@CurrentPrice", product.CurrentPrice.ToString("C", culture))
				.Replace("cid:product@MaxPrice", product.MaxPrice.ToString("C", culture))
				.Replace("cid:product@DifferencePrice", (product.MaxPrice - product.CurrentPrice).ToString("C", culture));

			MailMessage m = new MailMessage(from, to)
			{
				Subject = "Товар в наличии",
				Body = body,
				IsBodyHtml = true
			};
			m.Attachments.Add(new Attachment(logo) { ContentId = "email-logo@png" });
			m.Attachments.Add(new Attachment(warning) { ContentId = "email-warning@png" });

			SmtpClient smtp = new SmtpClient(configuration.GetSection("Configuration").GetSection("ServerEmail")["EmailSmtpHost"], int.Parse(configuration.GetSection("Configuration").GetSection("ServerEmail")["EmailSmtpPort"]))
			{
				Credentials = new NetworkCredential(serverEmail, configuration.GetSection("Configuration").GetSection("ServerEmail")["EmailPassword"]),
				EnableSsl = true
			};
			try
			{
				smtp.Send(m);
				result = true;
			}
			catch (Exception ex)
			{
				exResult = ex;
			}
			return result;
		}
	}
}
