using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace DarkAbaddonBot
{
    // Шаги стейт-машины для создания напоминания
    public enum NoteState
    {
        None,
        AwaitingText, // Бот ждет текст заметки
        AwaitingTime  // Бот ждет время напоминания
    }

    // Модель конкретного напоминания
    public class UserNote
    {
        public string NoteText { get; set; }        // Что напомнить
        public DateTime TargetTime { get; set; }    // Когда напомнить
        public bool IsFired { get; set; }           // Отправлено ли уже уведомление

        public UserNote(string noteText, DateTime targetTime)
        {
            NoteText = noteText;
            TargetTime = targetTime;
            IsFired = false;
        }
    }

    // Профессиональная модель пользователя, привязанная к JSON базе
    public class TelegramUser
    {
        public long TelegramId { get; set; }
        public string FirstName { get; set; }
        public List<UserNote> Notes { get; set; } // Бесконечный список заметок пользователя

        public TelegramUser(long telegramId, string firstName)
        {
            TelegramId = telegramId;
            FirstName = firstName ?? "Не указано";
            Notes = new List<UserNote>();
        }
    }

    class Program
    {
        private static string botToken = "8901066505:AAFSrc4NbFrlGU_N8lbNPTlHBCu2vxHlEU4";
        private static TelegramBotClient botClient;
        private static readonly string dbFilePath = "users_notes_db.json";
        private static Dictionary<string, List<string>> _brainContext = null;

        // Хранилище текущих состояний пользователей и их временных данных в ОЗУ
        private static Dictionary<long, NoteState> _userStates = new Dictionary<long, NoteState>();
        private static Dictionary<long, string> _tempNoteText = new Dictionary<long, string>();

        static async Task Main(string[] args)
        {
            // Запускаем твой алгоритм обучения ИИ при старте системы
            TrainMyAI();

            botClient = new TelegramBotClient(botToken);
            using var cts = new CancellationTokenSource();

            // ⚙️ ЗАПУСКАЕМ ФОНОВЫЙ ТАЙМЕР НАПОМИНАНИЙ (Сверяет время каждые 30 секунд)
            Timer alertTimer = new Timer(async _ => await CheckNotesAndNotifyAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandleErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token
            );

            var me = await botClient.GetMe();
            Console.WriteLine($"[Система] Бот-Планировщик @{me.Username} успешно запущен в сеть!");

            Console.ReadLine();
            cts.Cancel();
        }

        static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Type == UpdateType.Message && update.Message?.Text != null)
            {
                var message = update.Message;
                long userId = message.From.Id;
                string text = message.Text.Trim();

                Console.WriteLine($"[Чат] Входящий трафик от {message.From.FirstName}: {text}");

                // Проверяем/создаем профиль пользователя в JSON
                await EnsureUserExistsAsync(userId, message.From.FirstName);

                // --- ПОТОК СТЕЙТ-МАШИНЫ: ПОШАГОВАЯ ЗАПИСЬ ЗАМЕТКИ ---
                if (_userStates.TryGetValue(userId, out var currentState) && currentState != NoteState.None)
                {
                    await HandleNoteCreationAsync(bot, message, currentState, ct);
                    return;
                }

                // --- БЛОК ОБРАБОТКИ КОМАНД И НАЖАТИЙ REPLY-КНОПОК ---
                if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
                {
                    // Деплоим удобную шаблонную клавиатуру
                    var replyKeyboardMarkup = new ReplyKeyboardMarkup(new[]
                    {
                        new KeyboardButton[] { "➕ Создать заметку", "📋 Мои заметки" }
                    })
                    { ResizeKeyboard = true };

                    await bot.SendMessage(userId, "Здарова! Я твоя персональная Стелс-Напоминалка. Используй кнопки ниже, чтобы управлять задачами:", replyMarkup: replyKeyboardMarkup, cancellationToken: ct);
                    return;
                }

                if (text.Equals("➕ Создать заметку", StringComparison.OrdinalIgnoreCase))
                {
                    _userStates[userId] = NoteState.AwaitingText;
                    if (_tempNoteText.ContainsKey(userId)) _tempNoteText.Remove(userId);
                    await bot.SendMessage(userId, "Напиши текст заметки (например: сделать домашнее задание или выполнить норму на креативах):", cancellationToken: ct);
                    return;
                }

                if (text.Equals("📋 Мои заметки", StringComparison.OrdinalIgnoreCase))
                {
                    List<TelegramUser> users = await LoadUsersAsync();
                    var user = users.Find(u => u.TelegramId == userId);

                    if (user == null || user.Notes.Count == 0)
                    {
                        await bot.SendMessage(userId, "Твой список активных логов пуст. Нажми кнопку создания!", cancellationToken: ct);
                        return;
                    }

                    string notesList = "📋 ТВОИ АКТИВНЫЕ ЗАМЕТКИ:\n\n";
                    int count = 1;
                    foreach (var note in user.Notes)
                    {
                        string status = note.IsFired ? "✅ (Выполнено)" : "⏳ (Ожидает)";
                        notesList += $"{count}. {note.NoteText}\n📅 Время: {note.TargetTime:dd.MM.yyyy HH:mm} {status}\n\n";
                        count++;
                    }

                    await bot.SendMessage(userId, notesList, cancellationToken: ct);
                    return;
                }

                // Если это обычный текст — отправляем в ИИ на Цепях Маркова
                string aiResponse = GetAIResponse(text);
                await Task.Delay(4000, ct); // Твоя 4-секундная имитация ввода
                await bot.SendMessage(chatId: message.Chat.Id, text: aiResponse, cancellationToken: ct);
            }
        }

        // МЕНЕДЖЕР ОБРАБОТКИ ШАГОВ СОЗДАНИЯ ЗАМЕТКИ
        private static async Task HandleNoteCreationAsync(ITelegramBotClient bot, Message message, NoteState state, CancellationToken ct)
        {
            long userId = message.From.Id;
            string text = message.Text.Trim();

            if (state == NoteState.AwaitingText)
            {
                _tempNoteText[userId] = text;
                _userStates[userId] = NoteState.AwaitingTime;
                await bot.SendMessage(userId, "Текст записан. Теперь введи время напоминания в формате ДД.ММ.ГГГГ ЧЧ:ММ\nПример: 28.09.2026 23:45", cancellationToken: ct);
            }
            else if (state == NoteState.AwaitingTime)
            {
                // Парсим время строго по твоему шаблону
                if (DateTime.TryParseExact(text, "dd.MM.yyyy HH:mm", null, System.Globalization.DateTimeStyles.None, out DateTime parsedTime))
                {
                    if (parsedTime < DateTime.Now)
                    {
                        await bot.SendMessage(userId, "❌ Ошибка: нельзя отправить напоминание в прошлое! Введи корректное время:", cancellationToken: ct);
                        return;
                    }

                    // Дописываем заметку в массив конкретного юзера
                    List<TelegramUser> users = await LoadUsersAsync();
                    var user = users.Find(u => u.TelegramId == userId);
                    if (user != null)
                    {
                        user.Notes.Add(new UserNote(_tempNoteText[userId], parsedTime));
                        await SaveUsersAsync(users);
                    }

                    // Сбрасываем стейты в ОЗУ ноута
                    _userStates[userId] = NoteState.None;
                    _tempNoteText.Remove(userId);

                    await bot.SendMessage(userId, $"✅ Заметка успешно задеплоена в систему! Я напомню о ней точно в {text}.", cancellationToken: ct);
                }
                else
                {
                    await bot.SendMessage(userId, "❌ Неверный формат! Введи время строго по шаблону: ДД.ММ.ГГГГ ЧЧ:ММ\nПример: 29.09.2026 15:00", cancellationToken: ct);
                }
            }
        }

        // АСИНХРОННЫЙ ФОНОВЫЙ НАПОМИНАТЕЛЬ (Сверяет время по JSON базе данных)
        private static async Task CheckNotesAndNotifyAsync()
        {
            try
            {
                if (!File.Exists(dbFilePath)) return;

                List<TelegramUser> users = await LoadUsersAsync();
                bool needToSave = false;
                DateTime now = DateTime.Now;

                foreach (var user in users)
                {
                    foreach (var note in user.Notes)
                    {
                        // Если время пришло и пуш ещё не отправлялся
                        if (!note.IsFired && now >= note.TargetTime)
                        {
                            note.IsFired = true;
                            needToSave = true;

                            string alert = $"🚨 ВНИМАНИЕ, ТИМЛИД! НАПОМИНАНИЕ ПО ТВОЕМУ ТЗ:\n\n" +
                                           $"📌 {note.NoteText}\n\n" +
                                           $"Лог зафиксирован. Задача должна быть выполнена!";
                            await botClient.SendMessage(user.TelegramId, alert);
                        }
                    }
                }
                if (needToSave)
                {
                    await SaveUsersAsync(users); // Сохраняем флаги выполнения в файл
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Фоновый Таймер] Лаг проверки времени: {ex.Message}");
            }
        }
        // СИНХРОННЫЙ ГЕНЕРАТОР ЦЕПЕЙ МАРКОВА (ЖЕЛЕЗНЫЙ ФИКС ОШИБОК И ИНДЕКСОВ!)
        private static string GetAIResponse(string userMessage)
        {
            string msg = userMessage.ToLower().Trim();
            msg = msg.Replace(",", "").Replace(".", "").Replace("?", "").Replace("!", "");
            string[] words = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string jsonPath = "vocabulary.json";
            if (_brainContext == null && File.Exists(jsonPath))
            {
                string jsonText = File.ReadAllText(jsonPath);
                _brainContext = JsonConvert.DeserializeObject <Dictionary<string, List<string>>> (jsonText);
            }
            if (_brainContext != null && words.Length >= 1)
            {
                string startPair = "";
                for (int i = 0; i < words.Length - 1; i++)
                {
                    string pairCandidate = words[i] + " " + words[i + 1];
                    if (_brainContext.ContainsKey(pairCandidate))
                    {
                        startPair = pairCandidate;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(startPair))
                {
                    string currentPair = startPair;
                    string[] pairParts = startPair.Split(' ');
                    // ИСПРАВЛЕННЫЙ СИНТАКСИС: Передаем элементы по индексам [0] и!
                    List<string> generatedWords = new List<string> { pairParts[0], pairParts[1] };
                    Random rnd = new Random();
                    for (int i = 0; i < 12; i++)
                    {
                        if (_brainContext.ContainsKey(currentPair))
                        {
                            List<string> nextWordOptions = _brainContext[currentPair];
                            string predictedWord = nextWordOptions[rnd.Next(nextWordOptions.Count)];
                            generatedWords.Add(predictedWord);
                            currentPair = generatedWords[generatedWords.Count - 2] + " " + predictedWord;
                        }
                        else
                        {
                            break;
                        }
                    }
                    return string.Join(" ", generatedWords).Trim() + ".";
                }
            }
            // ЖЕЛЕЗНЫЙ ДЕФОЛТНЫЙ RETURN: убирает ошибку отсутствия возвращаемого значения!
            return "Лог сообщения принят. Чтобы создать напоминание, используй кнопку меню ниже 👇";
        }
        private static async Task EnsureUserExistsAsync(long telegramId, string firstName)
        {
            List<TelegramUser> users = await LoadUsersAsync();
            if (users.Exists(u => u.TelegramId == telegramId)) return;
            users.Add(new TelegramUser(telegramId, firstName));
            await SaveUsersAsync(users);
        }
        private static async Task<List<TelegramUser>> LoadUsersAsync()
        {
            if (!File.Exists(dbFilePath)) return new List<TelegramUser>();
            string json = await File.ReadAllTextAsync(dbFilePath);
            return JsonConvert.DeserializeObject<List<TelegramUser>>(json) ?? new List<TelegramUser>();
        }
        private static async Task SaveUsersAsync(List<TelegramUser> users)
        {
            string json = JsonConvert.SerializeObject(users, Formatting.Indented);
            await File.WriteAllTextAsync(dbFilePath, json);
        }
        static Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
        {
            Console.WriteLine($"[Ошибка] Баг сети Телеграм: {ex.Message}");
            return Task.CompletedTask;
        }
        static void TrainMyAI()
        {
            string bookPath = "training_book.txt";
            string jsonPath = "vocabulary.json";
            if (!File.Exists(bookPath))
            {
                Console.WriteLine("[Ошибка] Файл training_book.txt не найден! Не на чем обучаться.");
                return;
            }
            Console.WriteLine("[ИИ-Обучение] Запущен парсинг книги... Накатываем вежливую прошивку.");
            string rawText = File.ReadAllText(bookPath).ToLower();
            string cleanText = Regex.Replace(rawText, @"[^а-яё\s]", "");
            string[] allWords = cleanText.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var brainContext = new Dictionary<string, List<string>>();
            for (int i = 0; i < allWords.Length - 2; i++)
            {
                string keyPair = allWords[i] + " " + allWords[i + 1];
                string nextWord = allWords[i + 2];
                if (!brainContext.ContainsKey(keyPair))
                {
                    brainContext[keyPair] = new List<string>();
                }
                if (!brainContext[keyPair].Contains(nextWord))
                {
                    brainContext[keyPair].Add(nextWord);
                }
            }
            string jsonResult = JsonConvert.SerializeObject(brainContext, Formatting.Indented);
            File.WriteAllText(jsonPath, jsonResult);
            Console.WriteLine($"[ИИ-Обучение] Мозг успешно обучен! Создано пар: {brainContext.Count}");
        }
    }
}