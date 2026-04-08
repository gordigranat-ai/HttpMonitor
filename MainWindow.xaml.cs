using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HttpMonitor
{
    public partial class MainWindow : Window
    {
        // Серверные переменные
        private HttpListener? _listener;
        private bool _isRunning = false;
        private DateTime _startTime;

        // Статистика
        private int _getCount = 0;
        private int _postCount = 0;
        private readonly List<double> _responseTimes = new List<double>();

        // Логи
        private readonly List<LogEntry> _logs = new List<LogEntry>();

        // Таймер для обновления UI
        private readonly System.Windows.Threading.DispatcherTimer _timer;

        // Статический JsonSerializerOptions для переиспользования
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        // Текущий порт
        private int _currentPort = 8080;

        public MainWindow()
        {
            InitializeComponent();

            // Настройка таймера
            _timer = new System.Windows.Threading.DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => UpdateStats(); // Переименовано, чтобы не конфликтовало
            _timer.Start();

            // Инициализация
            UpdateSelfExampleButton();

            // Устанавливаем начальный статус
            UpdateServerStatus(false);
        }

        /// <summary>
        /// Обновляет отображение статуса сервера
        /// </summary>
        private void UpdateServerStatus(bool isRunning, int port = 0)
        {
            // Выполняем в потоке UI
            Dispatcher.Invoke(() =>
            {
                if (StatusText == null || StartBtn == null || StopBtn == null || PortBox == null)
                    return;

                if (isRunning)
                {
                    StatusText.Text = port > 0 ? $"Запущен (порт {port})" : "Запущен";
                    StatusText.Foreground = Brushes.Green;
                    StartBtn.IsEnabled = false;
                    StopBtn.IsEnabled = true;
                    PortBox.IsEnabled = false;
                }
                else
                {
                    StatusText.Text = "Остановлен";
                    StatusText.Foreground = Brushes.Red;
                    StartBtn.IsEnabled = true;
                    StopBtn.IsEnabled = false;
                    PortBox.IsEnabled = true;
                }
            });
        }

        /// <summary>
        /// Проверяет, свободен ли порт
        /// </summary>
        private bool IsPortFree(int port)
        {
            try
            {
                using var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Находит первый свободный порт, начиная с указанного
        /// </summary>
        private int FindFreePort(int startPort)
        {
            for (int port = startPort; port < startPort + 100; port++)
            {
                if (IsPortFree(port))
                {
                    return port;
                }
            }
            return -1; // Не найден свободный порт
        }

        // ==================== МЕТОДЫ СЕРВЕРА ====================

        private async void StartServer_Click(object sender, RoutedEventArgs e)
        {
            // Если сервер уже работает, не запускаем заново
            if (_isRunning)
            {
                AddLog("Сервер уже запущен", "SYSTEM");
                return;
            }

            try
            {
                if (PortBox == null)
                {
                    MessageBox.Show("Ошибка инициализации интерфейса", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                int requestedPort;
                if (!int.TryParse(PortBox.Text, out requestedPort) || requestedPort < 1 || requestedPort > 65535)
                {
                    requestedPort = 8080;
                }

                // Проверяем, свободен ли порт
                if (!IsPortFree(requestedPort))
                {
                    // Если порт занят, ищем свободный
                    int freePort = FindFreePort(requestedPort + 1);

                    if (freePort == -1)
                    {
                        MessageBox.Show($"Не удалось найти свободный порт. Все порты с {requestedPort} по {requestedPort + 100} заняты.",
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var result = MessageBox.Show(
                        $"Порт {requestedPort} занят. Использовать порт {freePort} вместо него?",
                        "Порт занят",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        requestedPort = freePort;
                        PortBox.Text = requestedPort.ToString();
                    }
                    else
                    {
                        return;
                    }
                }

                _currentPort = requestedPort;

                // Запускаем сервер
                await StartServerAsync(_currentPort);

                AddLog($"Сервер успешно запущен на порту {_currentPort}", "SYSTEM");

                // Обновляем пример для отправки на свой сервер
                UpdateSelfExampleButton();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка запуска сервера: {ex.Message}\n\n" +
                    "Возможно, нужны права администратора.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StartServerAsync(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _isRunning = true;
            _startTime = DateTime.Now;

            // Сброс статистики при запуске
            _getCount = 0;
            _postCount = 0;
            _responseTimes.Clear();
            _logs.Clear();

            // Очищаем логи в UI
            Dispatcher.Invoke(() =>
            {
                if (LogsListBox != null)
                    LogsListBox.Items.Clear();
                if (LoadGraphListBox != null)
                    LoadGraphListBox.Items.Clear();
                if (GetCountText != null)
                    GetCountText.Text = "0";
                if (PostCountText != null)
                    PostCountText.Text = "0";
                if (AvgTimeText != null)
                    AvgTimeText.Text = "0 ms";
                if (UptimeText != null)
                    UptimeText.Text = "00:00:00";
            });

            // Обновляем статус ПОСЛЕ запуска
            UpdateServerStatus(true, port);

            // Запускаем обработку запросов
            _ = Task.Run(() => ProcessRequestsAsync());
        }

        private async Task ProcessRequestsAsync()
        {
            while (_isRunning && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context));
                }
                catch (HttpListenerException)
                {
                    // Нормальное завершение при остановке
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        AddLog($"Ошибка при получении запроса: {ex.Message}", "ERROR");
                    }
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var startTime = DateTime.Now;
            string method = context.Request.HttpMethod;
            string url = context.Request.RawUrl ?? "/";

            // Безопасное чтение заголовков
            string headers = "";
            try
            {
                headers = string.Join("; ", context.Request.Headers.AllKeys
                    .Where(k => !string.IsNullOrEmpty(k))
                    .Select(k => $"{k}={context.Request.Headers[k]}"));
            }
            catch { }

            string body = "";
            if (context.Request.HasEntityBody)
            {
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                    body = await reader.ReadToEndAsync();
                }
                catch { }
            }

            AddLog($"Входящий {method} {url}", method);

            string responseText = "";
            int statusCode = 200;

            try
            {
                if (method == "GET")
                {
                    var uptime = DateTime.Now - _startTime;
                    responseText = JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        uptime = uptime.ToString(),
                        processed_gets = _getCount,
                        processed_posts = _postCount,
                        total_requests = _getCount + _postCount,
                        average_time_ms = _responseTimes.Count > 0 ? _responseTimes.Average() : 0,
                        server_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    }, _jsonOptions);
                    _getCount++;
                }
                else if (method == "POST")
                {
                    string receivedMessage = "пустое сообщение";
                    if (!string.IsNullOrEmpty(body))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("message", out var msgProp))
                            {
                                receivedMessage = msgProp.GetString() ?? "пустое сообщение";
                            }
                        }
                        catch
                        {
                            // Невалидный JSON - игнорируем
                        }
                    }

                    responseText = JsonSerializer.Serialize(new
                    {
                        id = Guid.NewGuid().ToString(),
                        message = $"Сохранено: {receivedMessage}",
                        timestamp = DateTime.Now,
                        original_body = body.Length > 200 ? body.Substring(0, 200) + "..." : body
                    }, _jsonOptions);
                    _postCount++;
                }
                else
                {
                    statusCode = 405;
                    responseText = "{\"error\":\"Method not allowed. Use GET or POST.\"}";
                }

                byte[] buffer = Encoding.UTF8.GetBytes(responseText);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer);
            }
            catch (Exception ex)
            {
                statusCode = 500;
                responseText = $"{{\"error\":\"{ex.Message}\"}}";
                AddLog($"Ошибка обработки: {ex.Message}", "ERROR");
            }
            finally
            {
                context.Response.OutputStream.Close();

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                _responseTimes.Add(elapsed);
                if (_responseTimes.Count > 1000) _responseTimes.RemoveAt(0);

                // Сохраняем полный лог
                _logs.Add(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Method = method,
                    Url = url,
                    StatusCode = statusCode,
                    Headers = headers,
                    Body = body.Length > 500 ? body.Substring(0, 500) + "..." : body,
                    ResponseBody = responseText.Length > 500 ? responseText.Substring(0, 500) + "..." : responseText,
                    ProcessingTimeMs = elapsed
                });

                // Обновляем фильтр
                Dispatcher.Invoke(() => ApplyFilter());
            }
        }

        private void StopServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_listener != null && _isRunning)
                {
                    AddLog("Остановка сервера...", "SYSTEM");

                    _isRunning = false;
                    _listener.Stop();
                    _listener.Close();
                    _listener = null;

                    // Обновляем статус сервера
                    UpdateServerStatus(false);

                    AddLog("Сервер остановлен", "SYSTEM");
                }
                else
                {
                    AddLog("Сервер не был запущен", "SYSTEM");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка при остановке сервера: {ex.Message}", "ERROR");
                // Принудительно сбрасываем статус
                _isRunning = false;
                UpdateServerStatus(false);
            }
        }

        // ==================== МЕТОДЫ КЛИЕНТА ====================

        private void MethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isPost = MethodBox.SelectedItem is ComboBoxItem item && item.Content.ToString() == "POST";
            if (BodyLabel != null)
                BodyLabel.Visibility = isPost ? Visibility.Visible : Visibility.Collapsed;
            if (BodyBox != null)
                BodyBox.Visibility = isPost ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void SendRequest_Click(object sender, RoutedEventArgs e)
        {
            await SendRequestAsync();
        }

        private async Task SendRequestAsync()
        {
            if (UrlBox == null || ResponseBox == null || SendBtn == null)
            {
                MessageBox.Show("Ошибка инициализации интерфейса", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string url = UrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Введите URL запроса", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string method = (MethodBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";
            string? body = method == "POST" ? BodyBox?.Text : null;

            SendBtn.IsEnabled = false;
            SendBtn.Content = "Отправка...";

            try
            {
                var (statusCode, responseBody) = await SendHttpRequestAsync(url, method, body);
                ResponseBox.Text = $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
                                   $"Статус: {statusCode}\n" +
                                   $"Время: {DateTime.Now:HH:mm:ss.fff}\n" +
                                   $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
                                   $"{responseBody}";
                AddLog($"Клиент: {method} {url} -> {statusCode}", "CLIENT");
            }
            catch (Exception ex)
            {
                ResponseBox.Text = $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
                                   $"ОШИБКА\n" +
                                   $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
                                   $"{ex.Message}";
                AddLog($"Ошибка клиента: {ex.Message}", "ERROR");
            }
            finally
            {
                SendBtn.IsEnabled = true;
                SendBtn.Content = "Отправить запрос";
            }
        }

        private static async Task<(int statusCode, string responseBody)> SendHttpRequestAsync(string url, string method, string? body)
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "WPF-HttpMonitor/1.0");

            HttpResponseMessage response;
            if (method == "GET")
            {
                response = await client.GetAsync(url);
            }
            else
            {
                var content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
                response = await client.PostAsync(url, content);
            }

            string responseBody = await response.Content.ReadAsStringAsync();

            // Форматируем JSON для красоты
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                responseBody = JsonSerializer.Serialize(doc, _jsonOptions);
            }
            catch
            {
                // Не JSON - оставляем как есть
            }

            return ((int)response.StatusCode, responseBody);
        }

        private void UpdateSelfExampleButton()
        {
            // Обновляем текст кнопки "POST: Отправить на свой сервер"
            Dispatcher.Invoke(() =>
            {
                var selfButton = FindButtonByTag("self");
                if (selfButton != null)
                {
                    int port = _isRunning ? _currentPort : 8080;
                    selfButton.Content = $"POST: Отправить на localhost:{port}";
                }
            });
        }

        private Button? FindButtonByTag(string tag)
        {
            // Ищем WrapPanel и в нём кнопку с нужным тегом
            if (this.Content is Grid mainGrid)
            {
                var tabControl = FindVisualChild<TabControl>(mainGrid);
                if (tabControl != null && tabControl.Items.Count > 1)
                {
                    var clientTab = tabControl.Items[1] as TabItem;
                    if (clientTab != null && clientTab.Content is ScrollViewer scrollViewer)
                    {
                        var grid = scrollViewer.Content as Grid;
                        if (grid != null && grid.Children.Count > 2)
                        {
                            var border = grid.Children[2] as Border;
                            if (border != null && border.Child is StackPanel stackPanel)
                            {
                                var wrapPanel = stackPanel.Children.OfType<WrapPanel>().FirstOrDefault();
                                if (wrapPanel != null)
                                {
                                    return wrapPanel.Children.OfType<Button>().FirstOrDefault(b => b.Tag?.ToString() == tag);
                                }
                            }
                        }
                    }
                }
            }
            return null;
        }

        private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }
            return null;
        }

        private async void Example_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            string? tag = btn?.Tag?.ToString();

            if (tag == "self")
            {
                if (_isRunning)
                {
                    UrlBox.Text = $"http://localhost:{_currentPort}/";
                    MethodBox.SelectedIndex = 1; // POST
                    if (BodyBox != null)
                        BodyBox.Text = "{\"message\": \"Тестовое сообщение от клиента\"}";
                    await SendRequestAsync();
                }
                else
                {
                    MessageBox.Show("Сервер не запущен. Сначала запустите сервер на вкладке 'HTTP Сервер'.",
                        "Сервер не запущен", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else if (!string.IsNullOrEmpty(tag))
            {
                UrlBox.Text = tag;
                MethodBox.SelectedIndex = 0; // GET
                await SendRequestAsync();
            }
        }

        // ==================== UI И ЛОГИРОВАНИЕ ====================

        private void AddLog(string message, string type)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logEntry = $"[{timestamp}] [{type}] {message}";

            Dispatcher.Invoke(() =>
            {
                if (LogsListBox != null)
                {
                    LogsListBox.Items.Insert(0, logEntry);
                    if (LogsListBox.Items.Count > 500)
                        LogsListBox.Items.RemoveAt(LogsListBox.Items.Count - 1);
                }
            });

            // Сохраняем в файл
            try
            {
                File.AppendAllText("logs.txt", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{type}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Игнорируем ошибки записи
            }
        }

        // Переименовано с UpdateUI на UpdateStats
        private void UpdateStats()
        {
            // Проверяем, что все элементы управления существуют
            if (GetCountText == null || PostCountText == null || AvgTimeText == null || UptimeText == null)
                return;

            if (!_isRunning)
            {
                GetCountText.Text = "0";
                PostCountText.Text = "0";
                AvgTimeText.Text = "0 ms";
                UptimeText.Text = "00:00:00";
                return;
            }

            GetCountText.Text = _getCount.ToString();
            PostCountText.Text = _postCount.ToString();

            double avg = _responseTimes.Count > 0 ? _responseTimes.Average() : 0;
            AvgTimeText.Text = $"{avg:F2} ms";

            var uptime = DateTime.Now - _startTime;
            UptimeText.Text = uptime.ToString(@"hh\:mm\:ss");

            UpdateLoadGraph();
        }

        private void UpdateLoadGraph()
        {
            if (LoadGraphListBox == null) return;

            if (!_isRunning)
            {
                Dispatcher.Invoke(() =>
                {
                    LoadGraphListBox.Items.Clear();
                    LoadGraphListBox.Items.Add("Сервер не запущен");
                });
                return;
            }

            try
            {
                var now = DateTime.Now;
                var lastMinutes = new SortedDictionary<DateTime, int>();

                // Безопасное создание минут для графика (последние 10 минут)
                for (int i = 9; i >= 0; i--)
                {
                    var minuteTime = now.AddMinutes(-i);
                    var minute = new DateTime(minuteTime.Year, minuteTime.Month, minuteTime.Day,
                                             minuteTime.Hour, minuteTime.Minute, 0);
                    lastMinutes[minute] = 0;
                }

                // Подсчёт запросов по минутам
                foreach (var log in _logs)
                {
                    var minute = new DateTime(log.Timestamp.Year, log.Timestamp.Month, log.Timestamp.Day,
                                             log.Timestamp.Hour, log.Timestamp.Minute, 0);
                    if (lastMinutes.ContainsKey(minute))
                    {
                        lastMinutes[minute]++;
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    LoadGraphListBox.Items.Clear();
                    int maxRequests = lastMinutes.Values.DefaultIfEmpty(0).Max();

                    foreach (var kv in lastMinutes)
                    {
                        int barLength = maxRequests > 0 ? (kv.Value * 40 / maxRequests) : 0;
                        string bar = new string('█', barLength);
                        LoadGraphListBox.Items.Add($"{kv.Key:HH:mm} │ {bar} {kv.Value} запр.");
                    }
                });
            }
            catch (Exception ex)
            {
                // Логируем ошибку, но не даём программе упасть
                Dispatcher.Invoke(() =>
                {
                    LoadGraphListBox.Items.Clear();
                    LoadGraphListBox.Items.Add($"Ошибка построения графика: {ex.Message}");
                });
            }
        }

        private void Filter_Changed(object sender, EventArgs e)
        {
            ApplyFilter();
        }

        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            if (MethodFilterBox != null)
                MethodFilterBox.SelectedIndex = 0;
            if (StatusFilterBox != null)
                StatusFilterBox.Text = "";
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (LogsListBox == null) return;

            string? method = (MethodFilterBox?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (method == "Все") method = null;

            int? status = null;
            if (StatusFilterBox != null && !string.IsNullOrWhiteSpace(StatusFilterBox.Text))
            {
                if (int.TryParse(StatusFilterBox.Text, out int s))
                    status = s;
            }

            var filtered = _logs.AsEnumerable();
            if (method != null)
                filtered = filtered.Where(l => l.Method == method);
            if (status.HasValue)
                filtered = filtered.Where(l => l.StatusCode == status.Value);

            Dispatcher.Invoke(() =>
            {
                LogsListBox.Items.Clear();
                foreach (var log in filtered.OrderByDescending(l => l.Timestamp).Take(100))
                {
                    LogsListBox.Items.Add($"[{log.Timestamp:HH:mm:ss}] [{log.Method}] {log.Url} -> {log.StatusCode} ({log.ProcessingTimeMs:F2}ms)");
                }
                if (!filtered.Any())
                    LogsListBox.Items.Add("Нет записей по выбранному фильтру");
            });
        }

        private void ExportLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = $"logs_export_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                var lines = _logs.Select(l => $"{l.Timestamp:yyyy-MM-dd HH:mm:ss.fff} | {l.Method} | {l.Url} | {l.StatusCode} | {l.ProcessingTimeMs:F2}ms");
                File.WriteAllLines(path, lines);
                MessageBox.Show($"Логи успешно экспортированы в файл:\n{path}", "Экспорт",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка экспорта: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // Модель лога
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string? Method { get; set; }
        public string? Url { get; set; }
        public int StatusCode { get; set; }
        public string? Headers { get; set; }
        public string? Body { get; set; }
        public string? ResponseBody { get; set; }
        public double ProcessingTimeMs { get; set; }
    }
}