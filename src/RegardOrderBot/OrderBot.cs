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
using System.Text;
using System.Security.Cryptography.X509Certificates;

namespace RegardOrderBot
{
	public class OrderBot : IHostedService
	{
		readonly ILogger<OrderBot> logger;
		readonly IHostApplicationLifetime hostAppLifetime;
		readonly List<Product> products = new List<Product>();
		readonly bool isHaveCommandLineArgs;
		readonly IServiceProvider serviceProvider;
		readonly public static CultureInfo culture = CultureInfo.CreateSpecificCulture("ru-RU");
		RegardParser regardParser;
		int artNumber;
		int maxPrice;
		public IHostApplicationLifetime Host
		{
			get
			{
				return hostAppLifetime;
			}
		}
		public int ProductId
		{
			get
			{
				return artNumber;
			}
		}
		public int ProductMaxPrice
		{
			get
			{
				return maxPrice;
			}
		}
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
			this.serviceProvider = serviceProvider;
			culture.NumberFormat.CurrencySymbol = "руб.";
			culture.NumberFormat.NumberDecimalSeparator = ".";
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
			regardParser = serviceProvider.GetService<RegardParser>();
			bool? isParserStarted = regardParser?.Start();
			if (isParserStarted == null)
			{
				logger.LogError($"Ошибка! не удалось получить сервис {nameof(RegardParser)}");
				hostAppLifetime.StopApplication();
			}else if (isParserStarted == true)
			{
				logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Парсер товаров успешно запущен.");
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

			var cert = await GetServerCertificateAsync("https://www.regard.ru");
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
				client.DefaultRequestHeaders.Referrer = new Uri($"https://www.regard.ru/catalog/tovar{product.ArtNumber}.htm");
				cookieContainer.Add(new Uri("https://www.regard.ru"), new Cookie("PHPSESSID", phpsessid));

				var message = new HttpRequestMessage(HttpMethod.Get, $"https://www.regard.ru/ajax/quick_order_small.php?good_id={product.ArtNumber}&type=1&fam=000&tel=11111111111&token={productToken}&tokenName=quick_order&close_button=false");
				message.Headers.Add("Cookie", $"PHPSESSID={phpsessid}");

				HttpResponseMessage response = await client.SendAsync(message);
				if (!response.IsSuccessStatusCode)
				{
					orderResult.TrackedStatus = RegardParser.TrackedStatus.FailOrderProcess;
				}
				else
				{
					var content = await response.Content.ReadAsStringAsync();
					orderResult.Content = content;
					orderResult.TrackedStatus = RegardParser.TrackedStatus.ProductOrdered;
				}
			}

			return await Task.FromResult(orderResult);
		}

		async Task<X509Certificate2> GetServerCertificateAsync(string url)
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
			
			//TODO: Отправить оповещение на Email о заказе товара
			
			regardParser.TrackingProducts[product.ArtNumber].Cancel();
		}
	}
}
