using Microsoft.Data.Sqlite;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace MoneyTracker
{
    public class SpendingRecord
    {
        public int RecordId { get; set; }
        public long OwnerId { get; set; }
        public decimal Cost { get; set; }
        public string SpendingType { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class MonthlyLimit
    {
        public long OwnerId { get; set; }
        public decimal LimitAmount { get; set; }
    }

    public class TrackerEngine
    {
        private ITelegramBotClient _bot;
        private Dictionary<long, string> _currentAction = new Dictionary<long, string>();
        private Dictionary<long, SpendingRecord> _tempRecords = new Dictionary<long, SpendingRecord>();

        public async Task StartTracking(string apiKey)
        {
            _bot = new TelegramBotClient(apiKey);

            PrepareStorage();

            var botInfo = await _bot.GetMeAsync();
            Console.WriteLine($"Tracker active: {botInfo.FirstName}");

            var cancelSource = new CancellationTokenSource();

            _bot.StartReceiving(
                ProcessUpdate,
                ProcessError,
                cancellationToken: cancelSource.Token
            );

            Console.WriteLine("Press Enter to stop...");
            Console.ReadLine();
            cancelSource.Cancel();
        }

        private void PrepareStorage()
        {
            using var db = new SqliteConnection("Data Source=spending.db");
            db.Open();

            var sql = db.CreateCommand();
            sql.CommandText = @"
                CREATE TABLE IF NOT EXISTS spending_log (
                    record_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    owner_id INTEGER,
                    cost REAL,
                    spending_type TEXT,
                    notes TEXT,
                    created_date TEXT
                );
                
                CREATE TABLE IF NOT EXISTS monthly_limits (
                    owner_id INTEGER PRIMARY KEY,
                    limit_amount REAL
                );";
            sql.ExecuteNonQuery();
        }

        private async Task ProcessUpdate(ITelegramBotClient bot, Update update, CancellationToken token)
        {
            try
            {
                if (update.Message != null)
                {
                    await ProcessMessage(bot, update.Message);
                }
                else if (update.CallbackQuery != null)
                {
                    await ProcessButtonClick(bot, update.CallbackQuery);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        private async Task ProcessMessage(ITelegramBotClient bot, Message message)
        {
            if (message.Text == null) return;

            var user = message.From.Id;
            var input = message.Text;

            if (_currentAction.ContainsKey(user))
            {
                await ContinueUserAction(bot, message, user, input);
                return;
            }

            switch (input)
            {
                case "/start":
                    await ShowMainScreen(bot, message.Chat.Id);
                    break;
                case "➕ Новый расход":
                    await StartNewSpending(bot, message.Chat.Id, user);
                    break;
                case "📋 Отчет неделя":
                    await ShowWeeklyReport(bot, message.Chat.Id, user);
                    break;
                case "📅 Отчет месяц":
                    await ShowMonthlyReport(bot, message.Chat.Id, user);
                    break;
                case "🎯 Лимит":
                    await SetupMonthlyLimit(bot, message.Chat.Id, user);
                    break;
                default:
                    if (input.StartsWith("/"))
                    {
                        await ProcessSlashCommand(bot, message, input);
                    }
                    break;
            }
        }

        private async Task ContinueUserAction(ITelegramBotClient bot, Message message, long user, string input)
        {
            var action = _currentAction[user];

            switch (action)
            {
                case "entering_cost":
                    await ProcessCostInput(bot, message, user, input);
                    break;
                case "entering_notes":
                    await ProcessNotesInput(bot, message, user, input);
                    break;
                case "setting_limit":
                    await ProcessLimitInput(bot, message, user, input);
                    break;
            }
        }

        private async Task ShowMainScreen(ITelegramBotClient bot, long chat)
        {
            var buttons = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("➕ Новый расход"), new KeyboardButton("📋 Отчет неделя") },
                new[] { new KeyboardButton("📅 Отчет месяц"), new KeyboardButton("🎯 Лимит") }
            })
            {
                ResizeKeyboard = true
            };

            var greeting = @"Добро пожаловать! 💰

Этот помощник отслеживает твои расходы:

➕ Новый расход - добавить трату
📋 Отчет неделя - статистика 7 дней
📅 Отчет месяц - траты за месяц
🎯 Лимит - установить бюджет

Начни с добавления расхода!";

            await bot.SendTextMessageAsync(chat, greeting, replyMarkup: buttons);
        }

        private async Task StartNewSpending(ITelegramBotClient bot, long chat, long user)
        {
            var types = new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🍕 Еда", "type_еда") },
                new[] { InlineKeyboardButton.WithCallbackData("🚕 Такси", "type_транспорт") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Квартира", "type_жилье") },
                new[] { InlineKeyboardButton.WithCallbackData("👖 Одежда", "type_одежда") },
                new[] { InlineKeyboardButton.WithCallbackData("💊 Аптека", "type_здоровье") },
                new[] { InlineKeyboardButton.WithCallbackData("🎬 Кино", "type_развлечения") }
            };

            var typeKeyboard = new InlineKeyboardMarkup(types);

            await bot.SendTextMessageAsync(chat, "Выбери категорию:", replyMarkup: typeKeyboard);
        }

        private async Task ProcessButtonClick(ITelegramBotClient bot, CallbackQuery click)
        {
            var user = click.From.Id;
            var data = click.Data;

            if (data.StartsWith("type_"))
            {
                var typeName = data.Replace("type_", "");
                _tempRecords[user] = new SpendingRecord
                {
                    OwnerId = user,
                    SpendingType = typeName,
                    CreatedAt = DateTime.Now
                };
                _currentAction[user] = "entering_cost";

                await bot.SendTextMessageAsync(click.Message.Chat.Id, "Сколько потратил?");
                await bot.AnswerCallbackQueryAsync(click.Id);
            }
        }

        private async Task ProcessCostInput(ITelegramBotClient bot, Message message, long user, string input)
        {
            if (decimal.TryParse(input.Replace(',', '.'), out decimal cost) && cost > 0)
            {
                _tempRecords[user].Cost = cost;
                _currentAction[user] = "entering_notes";

                await bot.SendTextMessageAsync(message.Chat.Id, "Добавь комментарий (или 'нет'):");
            }
            else
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "Введи нормальную сумму:");
            }
        }

        private async Task ProcessNotesInput(ITelegramBotClient bot, Message message, long user, string input)
        {
            var record = _tempRecords[user];
            record.Notes = input.ToLower() == "нет" ? "Без комментария" : input;

            StoreSpending(record);

            _currentAction.Remove(user);
            _tempRecords.Remove(user);

            await bot.SendTextMessageAsync(message.Chat.Id,
                $"✅ Записано!\nКатегория: {record.SpendingType}\nСумма: {record.Cost} руб.\nЗаметка: {record.Notes}");
        }

        private void StoreSpending(SpendingRecord record)
        {
            using var db = new SqliteConnection("Data Source=spending.db");
            db.Open();

            var sql = db.CreateCommand();
            sql.CommandText = @"
                INSERT INTO spending_log (owner_id, cost, spending_type, notes, created_date)
                VALUES ($owner, $cost, $type, $notes, $date)";

            sql.Parameters.AddWithValue("$owner", record.OwnerId);
            sql.Parameters.AddWithValue("$cost", record.Cost);
            sql.Parameters.AddWithValue("$type", record.SpendingType);
            sql.Parameters.AddWithValue("$notes", record.Notes);
            sql.Parameters.AddWithValue("$date", record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

            sql.ExecuteNonQuery();
        }

        private async Task ShowWeeklyReport(ITelegramBotClient bot, long chat, long user)
        {
            using var db = new SqliteConnection("Data Source=spending.db");
            db.Open();

            var sql = db.CreateCommand();
            sql.CommandText = @"
                SELECT spending_type, SUM(cost) as total 
                FROM spending_log 
                WHERE owner_id = $user AND created_date >= date('now', '-7 days')
                GROUP BY spending_type 
                ORDER BY total DESC";

            sql.Parameters.AddWithValue("$user", user);

            using var data = sql.ExecuteReader();
            var totals = new List<(string Type, decimal Amount)>();
            decimal overall = 0;

            while (data.Read())
            {
                var type = data.GetString(0);
                var amount = data.GetDecimal(1);
                totals.Add((type, amount));
                overall += amount;
            }

            if (totals.Count == 0)
            {
                await bot.SendTextMessageAsync(chat, "За неделю трат не было.");
                return;
            }

            var report = "📋 Итоги недели:\n\n";
            report += $"Всего: {overall:F2} руб.\n\n";

            foreach (var (type, amount) in totals)
            {
                var percent = (amount / overall) * 100;
                report += $"{type}: {amount:F2} руб. ({percent:F1}%)\n";
            }

            await bot.SendTextMessageAsync(chat, report);
        }

        private async Task ShowMonthlyReport(ITelegramBotClient bot, long chat, long user)
        {
            using var db = new SqliteConnection("Data Source=spending.db");
            db.Open();

            var sql = db.CreateCommand();
            sql.CommandText = @"
                SELECT spending_type, SUM(cost) as total 
                FROM spending_log 
                WHERE owner_id = $user AND strftime('%Y-%m', created_date) = strftime('%Y-%m', 'now')
                GROUP BY spending_type 
                ORDER BY total DESC";

            sql.Parameters.AddWithValue("$user", user);

            using var data = sql.ExecuteReader();
            var totals = new List<(string Type, decimal Amount)>();
            decimal overall = 0;

            while (data.Read())
            {
                var type = data.GetString(0);
                var amount = data.GetDecimal(1);
                totals.Add((type, amount));
                overall += amount;
            }

            if (totals.Count == 0)
            {
                await bot.SendTextMessageAsync(chat, "В этом месяце трат нет.");
                return;
            }

            var report = $"📅 Итоги {DateTime.Now:MMMM yyyy}:\n\n";
            report += $"Всего: {overall:F2} руб.\n\n";

            foreach (var (type, amount) in totals)
            {
                var percent = (amount / overall) * 100;
                report += $"{type}: {amount:F2} руб. ({percent:F1}%)\n";
            }

            await bot.SendTextMessageAsync(chat, report);
        }

        private async Task SetupMonthlyLimit(ITelegramBotClient bot, long chat, long user)
        {
            _currentAction[user] = "setting_limit";
            await bot.SendTextMessageAsync(chat, "Введи месячный лимит:");
        }

        private async Task ProcessLimitInput(ITelegramBotClient bot, Message message, long user, string input)
        {
            if (decimal.TryParse(input.Replace(',', '.'), out decimal limit) && limit > 0)
            {
                using var db = new SqliteConnection("Data Source=spending.db");
                db.Open();

                var sql = db.CreateCommand();
                sql.CommandText = @"
                    INSERT OR REPLACE INTO monthly_limits (owner_id, limit_amount)
                    VALUES ($user, $limit)";

                sql.Parameters.AddWithValue("$user", user);
                sql.Parameters.AddWithValue("$limit", limit);
                sql.ExecuteNonQuery();

                _currentAction.Remove(user);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Лимит {limit:F2} руб. установлен");
            }
            else
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "Нужно число больше нуля:");
            }
        }

        private async Task ProcessSlashCommand(ITelegramBotClient bot, Message message, string command)
        {
            switch (command)
            {
                case "/clean":
                    await ClearUserData(bot, message.Chat.Id, message.From.Id);
                    break;
            }
        }

        private async Task ClearUserData(ITelegramBotClient bot, long chat, long user)
        {
            using var db = new SqliteConnection("Data Source=spending.db");
            db.Open();

            var sql = db.CreateCommand();
            sql.CommandText = "DELETE FROM spending_log WHERE owner_id = $user";
            sql.Parameters.AddWithValue("$user", user);
            sql.ExecuteNonQuery();

            sql.CommandText = "DELETE FROM monthly_limits WHERE owner_id = $user";
            sql.ExecuteNonQuery();

            await bot.SendTextMessageAsync(chat, "✅ Данные очищены!");
        }

        private Task ProcessError(ITelegramBotClient bot, Exception error, CancellationToken token)
        {
            Console.WriteLine($"Problem: {error.Message}");
            return Task.CompletedTask;
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            var tracker = new TrackerEngine();
            await tracker.StartTracking("ТОКЕН");
        }
    }
}
