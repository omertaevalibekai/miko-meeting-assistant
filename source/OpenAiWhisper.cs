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
            if(key.Length<20||key.Any(c=>!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='-'||c=='_')))throw new ArgumentException("Paste your complete API key without spaces or extra characters.");
            byte[] clear=Store.Utf8.GetBytes(key);
            try { Store.Write(path??PathName,Convert.ToBase64String(ProtectedData.Protect(clear,null,DataProtectionScope.CurrentUser))); }
            finally { Array.Clear(clear,0,clear.Length); }
        }
        public static string Load(string path=null) {
            path=path??PathName;if(!File.Exists(path))return null;
            try {
                byte[] clear=ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(path)),null,DataProtectionScope.CurrentUser);
                try{return Store.Utf8.GetString(clear);}finally{Array.Clear(clear,0,clear.Length);}
            }catch {throw new IOException("Could not read the saved API key. Enter it again in ⋯ → Settings.");}
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
            if(String.IsNullOrWhiteSpace(key))throw new InvalidOperationException("Audio saved. Add your API key in ⋯ → Settings, then select the meeting and choose Retry.");
            token.ThrowIfCancellationRequested();
            var text=new StringBuilder();var srt=new StringBuilder();int index=0,part=0;
            using(var reader=new WaveFileReader(audio)) {
                var f=reader.WaveFormat;
                if(f.Encoding!=WaveFormatEncoding.Pcm||f.SampleRate!=16000||f.Channels!=1||f.BitsPerSample!=16)throw new IOException("The API requires mono 16 kHz PCM16 WAV audio.");
                long total=reader.Length,position=0;
                if(total==0)throw new IOException("The audio recording is empty.");
                while(position<total) {
                    token.ThrowIfCancellationRequested();
                    reader.Position=position;var data=new byte[(int)Math.Min(ChunkBytes,total-position)];
                    int read=0,n;while(read<data.Length&&(n=reader.Read(data,read,data.Length-read))>0)read+=n;
                    if(read==0)throw new IOException("Could not read the audio recording.");
                    int count=position+read<total?CutAtQuietBoundary(data,read):read;
                    double offset=position/32000.0,duration=count/32000.0;
                    progress("Whisper API · part "+(++part)+" · "+Math.Floor(position*100.0/total).ToString(CultureInfo.InvariantCulture)+"%");
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
                        } catch {throw new IOException("OpenAI returned an incomplete response. Audio saved; you can retry processing.");}
                    }
                }catch(TaskCanceledException){if(token.IsCancellationRequested)throw;throw new IOException("OpenAI did not respond within 10 minutes. Audio saved; try again later.");}
                catch(HttpRequestException){throw new IOException("Could not connect to OpenAI. Check your internet and retry. Your audio is saved.");}
            }
        }
        static string ErrorMessage(int status) {
            switch(status) {
                case 401:return "OpenAI rejected the API key. Check it in Settings. Your audio is saved.";
                case 403:return "OpenAI denied access. Check API availability and project permissions. Your audio is saved.";
                case 404:return "whisper-1 is unavailable to this project or has been retired by OpenAI. Your audio is saved.";
                case 429:return "OpenAI API limit reached. Check your balance and project limits, or try later. Your audio is saved.";
                default:return "OpenAI API error (HTTP "+status+"). Audio saved; you can retry processing.";
            }
        }
        static void Append(WhisperResponse result,double offset,double duration,StringBuilder text,StringBuilder srt,ref int index) {
            if(!String.IsNullOrWhiteSpace(result.text)&&(result.segments==null||result.segments.Count==0))throw new IOException("OpenAI returned no timestamps. Audio saved; please retry processing.");
            text.AppendLine(result.text.Trim());double previous=0;
            foreach(var segment in result.segments??new List<WhisperSegment>()) {
                if(String.IsNullOrWhiteSpace(segment.text))continue;
                if(!segment.start.HasValue||!segment.end.HasValue||Double.IsNaN(segment.start.Value)||Double.IsNaN(segment.end.Value)||Double.IsInfinity(segment.start.Value)||Double.IsInfinity(segment.end.Value))throw new IOException("OpenAI returned invalid timestamps.");
                double start=Math.Max(previous,Math.Min(duration,Math.Max(0,segment.start.Value))),end=Math.Min(duration,Math.Max(0,segment.end.Value));
                if(end<=start)throw new IOException("OpenAI returned an invalid speech interval.");
                srt.AppendLine((++index).ToString(CultureInfo.InvariantCulture));srt.AppendLine(Stamp(offset+start)+" --> "+Stamp(offset+end));srt.AppendLine(segment.text.Trim());srt.AppendLine();previous=end;
            }
        }
        static string Stamp(double seconds){return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\,fff",CultureInfo.InvariantCulture);}
    }
}
