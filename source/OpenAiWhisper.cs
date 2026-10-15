using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NAudio.Wave;

namespace MeetingSprite {
    public static class ApiKeyStore {
        public static string PathName { get { return Path.Combine(Store.Root,"openai-key.dpapi"); } }
        public static bool Exists { get { return File.Exists(PathName); } }
        public static void Save(string key,string path=null) {
            key=key.Trim();
            if(key.Length<20||key.Any(c=>!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='-'||c=='_')))throw new ArgumentException("Вставьте полный API-ключ без пробелов и посторонних символов.");
            byte[] clear=Store.Utf8.GetBytes(key);
            try { Store.Write(path??PathName,Convert.ToBase64String(ProtectedData.Protect(clear,null,DataProtectionScope.CurrentUser))); }
            finally { Array.Clear(clear,0,clear.Length); }
        }
        public static string Load(string path=null) {
            path=path??PathName;if(!File.Exists(path))return null;
            try {
                byte[] clear=ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(path)),null,DataProtectionScope.CurrentUser);
                try{return Store.Utf8.GetString(clear);}finally{Array.Clear(clear,0,clear.Length);}
            }catch {throw new IOException("Не удалось открыть сохранённый API-ключ. Введите его заново в «⋯ → Настройки».");}
        }
        public static void Delete(){if(Exists)File.Delete(PathName);}
    }
    public class WhisperSegment {
        public double? start {get;set;}
        public double? end {get;set;}
        public string text {get;set;}
    }
    public class WhisperResponse {
        public string text {get;set;}
        public List<WhisperSegment> segments {get;set;}
    }
    public class TranscriptionResult {
        public string Text,Srt;
    }
    public static class OpenAiWhisper {
        public const string Model="whisper-1";
        const string Endpoint="https://api.openai.com/v1/audio/transcriptions";
        // Ten minutes of PCM16/16kHz mono is 19.2 MB, below the API's 25 MB limit.
        const int ChunkBytes=600*32000;
        public static HttpClient CreateClient() {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            return new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(10)};
        }
        static int CutAtQuietBoundary(byte[] bytes,int count) {
            // Prefer a quiet quarter-second in the final 15 seconds of a full chunk.
            int best=count;double lowest=Double.MaxValue;
            for(int i=Math.Max(0,count-15*32000);i+8000<=count;i+=8000) {
                double energy=0;for(int p=i;p<i+8000;p+=2){double s=(short)(bytes[p]|bytes[p+1]<<8);energy+=s*s;}
                if(energy<=lowest){lowest=energy;best=i+4000;}
            }
            return best;
        }
        public static async Task<TranscriptionResult> Transcribe(string audio,string language,string key,Action<string> progress,CancellationToken token,HttpClient client) {
            if(String.IsNullOrWhiteSpace(key))throw new InvalidOperationException("Аудио сохранено. Добавьте API-ключ в «⋯ → Настройки», затем выберите встречу и нажмите «Повторить».");
            token.ThrowIfCancellationRequested();
            var text=new StringBuilder();var srt=new StringBuilder();int index=0,part=0;
            using(var reader=new WaveFileReader(audio)) {
                var f=reader.WaveFormat;
                if(f.Encoding!=WaveFormatEncoding.Pcm||f.SampleRate!=16000||f.Channels!=1||f.BitsPerSample!=16)throw new IOException("Для API требуется WAV PCM16, 16 кГц, моно.");
                long total=reader.Length,position=0;
                if(total==0)throw new IOException("Аудиозапись пуста.");
                while(position<total) {
                    token.ThrowIfCancellationRequested();
                    reader.Position=position;var data=new byte[(int)Math.Min(ChunkBytes,total-position)];
                    int read=0,n;while(read<data.Length&&(n=reader.Read(data,read,data.Length-read))>0)read+=n;
                    if(read==0)throw new IOException("Не удалось прочитать аудиозапись.");
                    int count=position+read<total?CutAtQuietBoundary(data,read):read;
                    double offset=position/32000.0,duration=count/32000.0;
                    progress("Whisper API · часть "+(++part)+" · "+Math.Floor(position*100.0/total).ToString(CultureInfo.InvariantCulture)+"%");
                    byte[] wav;
                    using(var stream=new MemoryStream()) {
                        using(var writer=new WaveFileWriter(stream,f)){writer.Write(data,0,count);writer.Flush();wav=stream.ToArray();}
                    }
                    var response=await Send(client,wav,language,key,token).ConfigureAwait(false);
                    Append(response,offset,duration,text,srt,ref index);
                    position+=count;
                }
            }
            return new TranscriptionResult{Text=text.ToString().Trim(),Srt=srt.ToString()};
        }
        static async Task<WhisperResponse> Send(HttpClient client,byte[] wav,string language,string key,CancellationToken token) {
            using(var request=new HttpRequestMessage(HttpMethod.Post,Endpoint))
            using(var form=new MultipartFormDataContent()) {
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
                form.Add(new StringContent(Model),"model");form.Add(new StringContent("verbose_json"),"response_format");
                form.Add(new StringContent("segment"),"timestamp_granularities[]");
                if(new[]{"ru","en","kk"}.Contains(language))form.Add(new StringContent(language),"language");
                var file=new ByteArrayContent(wav);file.Headers.ContentType=new MediaTypeHeaderValue("audio/wav");form.Add(file,"file","meeting.wav");
                request.Content=form;
                try {
                    using(var reply=await client.SendAsync(request,HttpCompletionOption.ResponseContentRead,token).ConfigureAwait(false)) {
                        if(!reply.IsSuccessStatusCode)throw new IOException(ErrorMessage((int)reply.StatusCode));
                        string json=await reply.Content.ReadAsStringAsync().ConfigureAwait(false);token.ThrowIfCancellationRequested();
                        try {
                            var result=new JavaScriptSerializer{MaxJsonLength=8*1024*1024}.Deserialize<WhisperResponse>(json);
                            if(result==null||result.text==null)throw new FormatException();
                            return result;
                        } catch {throw new IOException("OpenAI вернул неполный ответ. Аудио сохранено; можно повторить обработку.");}
                    }
                }catch(TaskCanceledException){if(token.IsCancellationRequested)throw;throw new IOException("OpenAI не ответил за 10 минут. Аудио сохранено; повторите обработку позже.");}
                catch(HttpRequestException){throw new IOException("Нет соединения с OpenAI. Проверьте интернет и повторите обработку. Аудио сохранено.");}
            }
        }
        static string ErrorMessage(int status) {
            switch(status) {
                case 401:return "OpenAI не принял API-ключ. Проверьте его в настройках. Аудио сохранено.";
                case 403:return "OpenAI запретил доступ. Проверьте доступность API и права проекта. Аудио сохранено.";
                case 404:return "Модель whisper-1 недоступна этому проекту или отключена OpenAI. Аудио сохранено.";
                case 429:return "Достигнут лимит OpenAI API. Проверьте баланс и лимиты проекта либо повторите позже. Аудио сохранено.";
                default:return "Ошибка OpenAI API (HTTP "+status+"). Аудио сохранено; можно повторить обработку.";
            }
        }
        static void Append(WhisperResponse result,double offset,double duration,StringBuilder text,StringBuilder srt,ref int index) {
            if(!String.IsNullOrWhiteSpace(result.text)&&(result.segments==null||result.segments.Count==0))throw new IOException("OpenAI не вернул таймкоды. Аудио сохранено; повторите обработку.");
            text.AppendLine(result.text.Trim());double previous=0;
            foreach(var segment in result.segments??new List<WhisperSegment>()) {
                if(String.IsNullOrWhiteSpace(segment.text))continue;
                if(!segment.start.HasValue||!segment.end.HasValue||Double.IsNaN(segment.start.Value)||Double.IsNaN(segment.end.Value)||Double.IsInfinity(segment.start.Value)||Double.IsInfinity(segment.end.Value))throw new IOException("OpenAI вернул некорректные таймкоды.");
                double start=Math.Max(previous,Math.Min(duration,Math.Max(0,segment.start.Value))),end=Math.Min(duration,Math.Max(0,segment.end.Value));
                if(end<=start)throw new IOException("OpenAI вернул некорректный интервал речи.");
                srt.AppendLine((++index).ToString(CultureInfo.InvariantCulture));srt.AppendLine(Stamp(offset+start)+" --> "+Stamp(offset+end));srt.AppendLine(segment.text.Trim());srt.AppendLine();previous=end;
            }
        }
        static string Stamp(double seconds){return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\,fff",CultureInfo.InvariantCulture);}
    }
}
