using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ProbSoftware
{
    internal class Program
    {
        private static string apiKey = Environment.GetEnvironmentVariable("TMDB_API_KEY"); 
        private static readonly HttpClient client = new HttpClient();

        private static HashSet<int> banliFilmler = new HashSet<int>();
        private static HashSet<int> banliDiziler = new HashSet<int>();
        private static HashSet<int> hassasFilmler = new HashSet<int>();
        private static HashSet<int> hassasDiziler = new HashSet<int>();

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== PROB SOFTWARE VERI MADENCILIGI V7.1 (DESYNC KORUMALI) ===");

            if (string.IsNullOrEmpty(apiKey))
            {
                Console.WriteLine("\n[HATA] TMDB_API_KEY bulunamadı!");
                return;
            }

            string statePath = Path.Combine(Directory.GetCurrentDirectory(), "islenen_keywords.json");
            string dataPath = Path.Combine(Directory.GetCurrentDirectory(), "filter_data.json");
            MinerState state = new MinerState();

            // --- 0. FAZ: HAFIZAYI YÜKLEME ---
            if (File.Exists(dataPath))
            {
                try
                {
                    var oldData = JsonConvert.DeserializeObject<ExportData>(File.ReadAllText(dataPath));
                    if (oldData != null)
                    {
                        if (oldData.yasakli?.filmler != null) banliFilmler = new HashSet<int>(oldData.yasakli.filmler);
                        if (oldData.yasakli?.diziler != null) banliDiziler = new HashSet<int>(oldData.yasakli.diziler);
                        if (oldData.hassas?.filmler != null) hassasFilmler = new HashSet<int>(oldData.hassas.filmler);
                        if (oldData.hassas?.diziler != null) hassasDiziler = new HashSet<int>(oldData.hassas.diziler);
                    }
                }
                catch { }
            }

            if (File.Exists(statePath))
            {
                try
                {
                    state = JsonConvert.DeserializeObject<MinerState>(File.ReadAllText(statePath)) ?? new MinerState();
                }
                catch { }
            }

            var banliKeywords = GetBanliList().Distinct().ToList();
            var hassasKeywords = GetHassasList().Distinct().ToList();

            string sonTarihFiltresi = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");

            // --- SÖZLÜK OLUŞTURMA FAZI ---
            await GuncelleKeywordIsimleriTxtAsync(banliKeywords, hassasKeywords);

            // --- 1. FAZ: YETİŞKİN TARAMASI ---
            Console.WriteLine("\n--- [FAZ 1] YETISKIN ICERIK TARANIYOR ---");
            foreach (var kwId in banliKeywords)
            {
                bool isNewKeyword = !state.IslenenYetiskinKeywords.Contains(kwId);
                string dateFilter = isNewKeyword ? null : sonTarihFiltresi; 

                if (isNewKeyword) Console.WriteLine($"\n[YENİ KEYWORD] KW:{kwId} bulundu! Tüm geçmiş taranıyor...");
                else Console.WriteLine($"\n[GÜNLÜK KONTROL] KW:{kwId} için sadece {sonTarihFiltresi} sonrasi taranıyor...");

                int eklendiFilm = await FetchData(kwId, "movie", banliFilmler, null, "🔞 YET-FILM", dateFilter);
                int eklendiDizi = await FetchData(kwId, "tv", banliDiziler, null, "🔞 YET-DIZI", dateFilter);

                // GÜVENLİK (DESYNC) ÇÖZÜMÜ
                if (isNewKeyword || eklendiFilm > 0 || eklendiDizi > 0)
                {
                    if (isNewKeyword) state.IslenenYetiskinKeywords.Add(kwId);
                    await File.WriteAllTextAsync(statePath, JsonConvert.SerializeObject(state));
                    await FinalizeAndExportJson(dataPath); 
                }
            }

            // --- 2. FAZ: HASSAS TARAMASI ---
            Console.WriteLine("\n--- [FAZ 2] HASSAS ICERIK TARANIYOR ---");
            foreach (var kwId in hassasKeywords)
            {
                bool isNewKeyword = !state.IslenenHassasKeywords.Contains(kwId);
                string dateFilter = isNewKeyword ? null : sonTarihFiltresi;

                if (isNewKeyword) Console.WriteLine($"\n[YENİ KEYWORD] KW:{kwId} bulundu! Tüm geçmiş taranıyor...");
                else Console.WriteLine($"\n[GÜNLÜK KONTROL] KW:{kwId} için sadece {sonTarihFiltresi} sonrasi taranıyor...");

                int eklendiFilm = await FetchData(kwId, "movie", hassasFilmler, banliFilmler, "⚠️ HAS-FILM", dateFilter);
                int eklendiDizi = await FetchData(kwId, "tv", hassasDiziler, banliDiziler, "⚠️ HAS-DIZI", dateFilter);

                if (isNewKeyword || eklendiFilm > 0 || eklendiDizi > 0)
                {
                    if (isNewKeyword) state.IslenenHassasKeywords.Add(kwId);
                    await File.WriteAllTextAsync(statePath, JsonConvert.SerializeObject(state));
                    await FinalizeAndExportJson(dataPath);
                }
            }

            Console.WriteLine("\n" + new string('=', 50));
            Console.WriteLine("🏁 ISLEM TAMAMLANDI! Tüm yeni veriler dosyaya işlendi.");
        }

        static async Task<int> FetchData(int keywordId, string type, HashSet<int> targetSet, HashSet<int> upperSet, string logTag, string dateFilter)
        {
            int page = 1;
            int totalPages = 1;
            int addedInThisKeyword = 0;

            string dateParamName = type == "movie" ? "primary_release_date.gte" : "first_air_date.gte";

            try
            {
                do
                {
                    string url = $"https://api.themoviedb.org/3/discover/{type}?api_key={apiKey}&with_keywords={keywordId}&page={page}&include_adult=false";
                    if (!string.IsNullOrEmpty(dateFilter))
                    {
                        url += $"&{dateParamName}={dateFilter}";
                    }

                    var response = await client.GetAsync(url);
                    if (!response.IsSuccessStatusCode) break;

                    var content = await response.Content.ReadAsStringAsync();
                    var data = JsonConvert.DeserializeObject<TmdbResponse>(content);
                    totalPages = data.total_pages;

                    if (data.results != null)
                    {
                        foreach (var item in data.results)
                        {
                            if (upperSet != null && upperSet.Contains(item.id)) continue;

                            if (targetSet.Add(item.id))
                            {
                                addedInThisKeyword++;
                            }
                        }
                    }

                    Console.Write($"\r[ISTEK] {logTag} | Sayfa:{page}/{totalPages} | Yeni Eklenen ID:{addedInThisKeyword}     ");
                    page++;
                    await Task.Delay(80); 

                } while (page <= totalPages && page <= 500);
                Console.WriteLine(); 
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[HATA] {ex.Message}");
            }
            return addedInThisKeyword;
        }

        static async Task FinalizeAndExportJson(string currentPath)
        {
            var exportData = new ExportData
            {
                son_guncelleme = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                istatistikler = new Istatistikler
                {
                    toplam_yasakli = banliFilmler.Count + banliDiziler.Count,
                    toplam_hassas = hassasFilmler.Count + hassasDiziler.Count
                },
                yasakli = new Kategori
                {
                    filmler = banliFilmler.OrderBy(x => x).ToList(),
                    diziler = banliDiziler.OrderBy(x => x).ToList()
                },
                hassas = new Kategori
                {
                    filmler = hassasFilmler.OrderBy(x => x).ToList(),
                    diziler = hassasDiziler.OrderBy(x => x).ToList()
                }
            };

            string jsonOutput = JsonConvert.SerializeObject(exportData, Formatting.None); 
            await File.WriteAllTextAsync(currentPath, jsonOutput);
        }

        static async Task GuncelleKeywordIsimleriTxtAsync(List<int> banli, List<int> hassas)
        {
            Console.WriteLine("\n--- [FAZ 0.5] KEYWORD SÖZLÜĞÜ GÜNCELLENİYOR ---");
            string txtPath = Path.Combine(Directory.GetCurrentDirectory(), "keyword_list.txt");
            Dictionary<int, string> keywordSozlugu = new Dictionary<int, string>();

            if (File.Exists(txtPath))
            {
                var lines = File.ReadAllLines(txtPath);
                foreach (var line in lines)
                {
                    if (line.StartsWith("ID: "))
                    {
                        var parts = line.Split('|');
                        if (parts.Length == 2)
                        {
                            var idStr = parts[0].Replace("ID:", "").Trim();
                            var nameStr = parts[1].Replace("İsim:", "").Trim();
                            if (int.TryParse(idStr, out int id))
                            {
                                keywordSozlugu[id] = nameStr;
                            }
                        }
                    }
                }
            }

            bool degisiklikOldu = false;
            var tumKeywords = banli.Concat(hassas).Distinct().ToList();

            foreach (var kwId in tumKeywords)
            {
                if (!keywordSozlugu.ContainsKey(kwId) || keywordSozlugu[kwId] == "Bilinmiyor")
                {
                    string kwName = await FetchKeywordName(kwId);
                    keywordSozlugu[kwId] = kwName;
                    degisiklikOldu = true;
                    Console.WriteLine($"[SÖZLÜĞE EKLENDİ] ID: {kwId} -> {kwName}");
                    await Task.Delay(40); 
                }
            }

            if (degisiklikOldu || !File.Exists(txtPath))
            {
                using (StreamWriter sw = new StreamWriter(txtPath, false, System.Text.Encoding.UTF8)) 
                {
                    sw.WriteLine("=========================================");
                    sw.WriteLine("        BANNED KEYWORDS");
                    sw.WriteLine("=========================================\n");
                    foreach (var id in banli)
                    {
                        string name = keywordSozlugu.ContainsKey(id) ? keywordSozlugu[id] : "Bilinmiyor";
                        sw.WriteLine($"ID: {id} | İsim: {name}");
                    }

                    sw.WriteLine("\n\n=========================================");
                    sw.WriteLine("           SENSITIVE KEYWORDS");
                    sw.WriteLine("=========================================\n");
                    foreach (var id in hassas)
                    {
                        string name = keywordSozlugu.ContainsKey(id) ? keywordSozlugu[id] : "Bilinmiyor";
                        sw.WriteLine($"ID: {id} | İsim: {name}");
                    }
                }
                Console.WriteLine($"[BAŞARILI] 'keyword_list.txt' dosyası güncellendi ve kaydedildi!");
            }
            else
            {
                Console.WriteLine("[BİLGİ] Yeni keyword yok, sözlük TXT dosyası zaten güncel.");
            }
        }

        static async Task<string> FetchKeywordName(int keywordId)
        {
            try
            {
                var response = await client.GetAsync($"https://api.themoviedb.org/3/keyword/{keywordId}?api_key={apiKey}");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var data = JsonConvert.DeserializeObject<TmdbKeyword>(content);
                    return data?.name ?? "Bilinmiyor";
                }
            }
            catch { }
            return "Bilinmiyor";
        }

        public class TmdbResponse { public int total_pages { get; set; } public List<TmdbResult> results { get; set; } }
        public class TmdbResult { public int id { get; set; } }
        public class TmdbKeyword { public int id { get; set; } public string name { get; set; } }

        public class MinerState
        {
            public List<int> IslenenYetiskinKeywords { get; set; } = new List<int>();
            public List<int> IslenenHassasKeywords { get; set; } = new List<int>();
        }

        public class ExportData
        {
            public string son_guncelleme { get; set; }
            public Istatistikler istatistikler { get; set; }
            public Kategori yasakli { get; set; }
            public Kategori hassas { get; set; }
        }

        public class Istatistikler { public int toplam_yasakli { get; set; } public int toplam_hassas { get; set; } }
        public class Kategori { public List<int> filmler { get; set; } public List<int> diziler { get; set; } }

        // --- KEYWORD LİSTELERİ ---
        static List<int> GetBanliList() => new List<int> { 356759, 445, 272027, 2727, 246422, 15197, 18314, 18321, 154986, 334276, 176511, 358744, 360750, 360980, 348517, 364927, 187522, 190366, 238355, 277271, 7344, 283935, 314985, 330968, 334945, 335703, 335853, 341367, 193698, 331516, 158436, 345881, 360071, 347722, 364526, 155139, 331947, 319453, 334901, 334903, 322288, 359554, 364146, 5593, 6443, 333807, 301766, 345933, 368563, 256466, 190370, 238059, 256603, 226161, 343572, 344391, 219371, 343713, 361488, 365656, 328992, 334900, 325692, 325693, 352503, 350793, 355313, 198385, 353113, 353318, 214564, 365272, 360629, 195997, 342167, 320667, 358445, 358446, 341305, 362365, 283206, 284535, 253976, 253977, 336557, 247411, 284609, 155477, 332190, 323678, 303659, 315168, 158713, 323690, 358926, 356830, 366328, 344904, 349176, 236972, 354880, 359981, 367629, 362559, 349634, 347060, 324746, 281532, 362757, 41260, 158718, 271115, 195624, 280179, 243575, 243577, 316515, 316516, 224000, 173669, 357875, 270245, 348621, 353629, 275749, 313433, 165614, 356130, 295736, 353763, 367808, 163037, 363827, 353079, 367576, 156331, 369328, 267122, 186071, 186103, 186107, 2426, 238809, 238858, 239123, 195918, 9606, 199723, 204207, 206789, 243665, 9835, 10053, 282903, 33434, 33439, 33441, 155761, 254453, 254535, 167554, 229194, 229706, 171134, 171341, 232090, 232091, 232651, 335184, 335205, 176792, 324962, 270176, 326192, 348034, 348091, 348092, 361837, 361838, 349796, 368141, 186369, 187237, 187551, 235653, 190178, 272376, 272673, 274689, 274690, 11486, 11495, 313308, 329280, 215290, 33998, 159595, 225001, 256497, 256538, 166136, 227758, 333465, 333576, 229776, 335048, 335762, 303750, 338673, 360010, 360014, 338910, 349458, 350144, 350504, 350556, 190014, 190015, 236878, 237017, 273123, 195376, 306051, 211069, 211291, 249844, 33679, 217311, 218005, 316365, 228853, 295963, 297195, 356388, 356737, 356749, 359767, 360166, 349035, 349893, 350362, 280017, 11512, 11474, 11531, 245541, 11865, 213493, 249188, 155301, 332013, 332014, 332015, 332639, 161350, 228181, 232938, 262784, 262788, 357817, 183728, 339349, 340713, 363160, 363162, 354470, 355768, 213132, 213286, 155535, 156434, 332109, 332189, 292340, 321739, 232489, 172454, 300276, 336003, 184115, 350707, 352180, 353993, 353994, 355949, 242622, 18087, 10735, 265021, 180670, 275267, 239037, 265020, 350503, 350552, 215876, 324621, 357870, 361958, 332040, 340472,570, 180670, 350503, 350552, 357870, 13083, 350770, 235069, 332040, 315583, 15241, 184352, 340472, 361958, 330749, 18087, 351293, 290035, 319577, 215876, 242622, 10735, 265021, 275267, 239037, 265020, 324621, 33841, 350705, 350708, 350360, 350361, 320650, 11792, 350502, 350503, 350553, 367095, 363905, 362000, 291725, 352765, 159551, 14794, 278381, 358926, 297008, 157812, 341156, 155262, 212725, 345890, 9838, 197505, 33439, 18077, 155139, 5919, 202572, 347941,566, 549, 739, 459, 571, 366, 5919, 2699, 3183, 2316, 2843, 2943, 5906, 5489, 4569, 6593, 5902, 7098, 11315, 8640, 9331, 11527, 11534, 11516, 9627, 10850, 10180, 10427, 11722, 11262, 11275, 11340, 12365, 13059, 15048, 15049, 15130, 15136, 33625, 34115, 41515, 156731, 157096, 160934, 163791, 164263, 164865, 168812, 171219, 171662, 173251, 173662, 173672, 175529, 178649, 179385, 180340, 184163, 184988, 186410, 186621, 189409, 190327, 190372, 190751, 193316, 196399, 196478, 208135, 208271, 208960, 211088, 214501, 221445, 224888, 225582, 226543, 227783, 228930, 229801, 230905, 231169, 236454, 236521, 239192, 239239, 240109, 240305, 241179, 243748, 245045, 247099, 247821, 248835, 249749, 253337, 254199, 256422, 257264, 258533, 259285, 262404, 262497, 262765, 263807, 264411, 264551, 265119, 265777, 267923, 269113, 269644, 271167, 272617, 273129, 274056, 275157, 278015, 279038, 283291, 284580, 287417, 288441, 290166, 291605, 293495, 302032, 302136, 302602, 305436, 305583, 312906, 312913, 314187, 315129, 315205, 315279, 315665, 317191, 319594, 322141, 323806, 324058, 324307, 324605, 324920, 325223, 325395, 325602, 327908, 329424, 329922, 329968, 333088, 332953, 333976, 334115, 336006, 336035, 337111, 337688, 337701, 337780, 338318, 338720, 343560, 344841, 345865, 346460, 346492, 346687, 346769, 347045, 347179, 347864, 348203, 348903, 349323, 349430, 351677, 352268, 353537, 353920, 354264, 354581, 354582, 354597, 354619, 354762, 355043, 355146, 355881, 356041, 356248, 356309, 356740, 356741, 356742, 356743, 356744, 356745, 356746, 357056, 357180, 357263, 357264, 357415, 357518, 357873, 359231, 359237, 359574, 361425, 361433, 362617, 362724, 363345, 363409, 363912, 364525, 365124, 365562, 365727, 366428, 367043, 367871, 368074, 368137, 368153, 368154, 368294, 368331, 368366, 368905, 369732, 369903, 370111, 370276, 370589, 370590, 370714, 370770, 370948, 370976, 371058, 371062, 371256};
        static List<int> GetHassasList() => new List<int> { 199758, 299096, 368996, 318070, 262786, 347799, 341441, 1664, 207767, 364719, 240530, 298666, 238098, 192628, 226010, 337325, 192119, 363964, 596, 193924, 357928, 337525, 248288, 258593, 180393, 315444, 161919, 362782, 358266, 337153, 11192, 299568, 327622, 33451, 33432, 345093, 365518, 363786, 249325, 3201, 302443, 173511, 337387, 282251, 349391, 328767, 361496, 281741, 360081, 359980, 359982, 360333, 292429, 309326, 340823, 361114, 170827, 348414, 327476, 352329, 325099, 11518, 11530, 323771, 155740, 343982, 319919, 353450, 335637, 236487, 352947, 365586, 309121, 4426, 322607, 325483,2039, 738, 3456, 5436, 4316, 7017, 7066, 11316, 9344, 11536, 11190, 15128, 18732, 41404, 155691, 156777, 156930, 160926, 162804, 189361, 189464, 190115, 190828, 192188, 192856, 194921, 195664, 195669, 195999, 197100, 197314, 197758, 207807, 210088, 212023, 215577, 220108, 224663, 225433, 226458, 228680, 232868, 244110, 245055, 245262, 251165, 251978, 253825, 253979, 254565, 259284, 260540, 267804, 269784, 275796, 279043, 281960, 292742, 295750, 296912, 300473, 300850, 302868, 306327, 308594, 315241, 315721, 317472, 327357, 335570, 339284, 340842, 340843, 342501, 343098, 345799, 346613, 346909, 347798, 347800, 348418, 349655, 349894, 351276, 351612, 354474, 354475, 354477, 354479, 356634, 356732, 356733, 357057, 357460, 359518, 360922, 360998, 362572, 365846, 367133, 368139, 370520};
    }
}
