using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingSprite {
    public class Meeting {
        public string Title { get; set; }
        public string Started { get; set; }
        public string Status { get; set; }
        public string Mode { get; set; }
        public string Language { get; set; }
        public string TranscriptionModel { get; set; }
        public string Microphone { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public string Warning { get; set; }
        public double Seconds { get; set; }
        [ScriptIgnore] public string Folder { get; set; }
        [ScriptIgnore] public string Caption { get { return Title + "\n" + Started + "  ·  " + StatusLabel; } }
        [ScriptIgnore] public string StatusLabel { get {
            switch (Status) {
                case "ready": return "Готово";
                case "recording": return "Запись прервана";
                case "processing": return "Обработка прервана";
                case "failed": return "Нужна повторная обработка";
                default: return "Аудио сохранено";
            }
        } }
    }
    public class Settings {
        public string Mic { get; set; }
        public string Output { get; set; }
        public string Language { get; set; }
        public bool Online { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public int Mascot { get; set; }
        public bool? Animate { get; set; }
        public string TranscriptionProvider { get; set; }
    }
    public static class Store {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Meetings = Path.Combine(Root, "Meetings");
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public static void Write(string path, string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, Utf8);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public static void Save(Meeting m) { Write(Path.Combine(m.Folder, "meeting.json"), new JavaScriptSerializer().Serialize(m)); }
        public static List<Meeting> List() {
            Directory.CreateDirectory(Meetings);
            var result = new List<Meeting>();
            foreach (string dir in Directory.GetDirectories(Meetings).OrderByDescending(x => x)) {
                try {
                    var m = new JavaScriptSerializer().Deserialize<Meeting>(File.ReadAllText(Path.Combine(dir,"meeting.json"), Utf8));
                    m.Folder = dir; result.Add(m);
                } catch { /* Keep damaged directories on disk; do not delete user files. */ }
            }
            return result;
        }
        public static Settings LoadSettings() {
            try { return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Path.Combine(Root,"settings.json"))); }
            catch { return new Settings { Language="ru", Left=-1, Top=-1 }; }
        }
        public static void SaveSettings(Settings s) { Write(Path.Combine(Root,"settings.json"),new JavaScriptSerializer().Serialize(s)); }
        public static Meeting Create(string title, bool online, string lang, string mic, string output) {
            var m = new Meeting { Title=String.IsNullOrWhiteSpace(title) ? "Встреча" : title.Trim(),
                Started=DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz"), Mode=online ? "online" : "room",
                Language=lang, Microphone=mic, Output=output, Status="recording",
                Folder=Path.Combine(Meetings,DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")+"_"+Guid.NewGuid().ToString("N").Substring(0,6)) };
            Directory.CreateDirectory(m.Folder); Save(m); return m;
        }
        public static void Open(string path) { Process.Start(new ProcessStartInfo(path) { UseShellExecute=true }); }
    }
    public class DeviceChoice {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name; }
        public static List<DeviceChoice> Get(DataFlow flow) {
            var result = new List<DeviceChoice>();
            using (var e = new MMDeviceEnumerator()) {
                string defaultId = null;
                try { using (var d = e.GetDefaultAudioEndpoint(flow, Role.Multimedia)) defaultId=d.ID; } catch { }
                foreach (var d in e.EnumerateAudioEndPoints(flow,DeviceState.Active)) {
                    result.Add(new DeviceChoice { Id=d.ID, Name=d.FriendlyName+(d.ID==defaultId ? " (по умолчанию)" : "") }); d.Dispose();
                }
                result=result.OrderByDescending(d=>d.Id==defaultId).ToList();
            }
            return result;
        }
    }
    // Each stream uses one shared monotonic clock. Long discontinuities are padded,
    // so a silent loopback interval never shifts later voices towards the start.
    public class TimedTrack : IDisposable {
        readonly WaveFileWriter writer;
        readonly object gate = new object();
        readonly Stopwatch clock;
        readonly byte[] zeros = new byte[65536];
        bool closed;
        double lastFlush;
        public float Peak;
        public long BytesReceived;
        public TimedTrack(string path, WaveFormat format, Stopwatch clock) { writer=new WaveFileWriter(path,format); this.clock=clock; }
        long TimeBytes(double seconds) { long n=(long)(seconds*writer.WaveFormat.AverageBytesPerSecond); return n-n%writer.WaveFormat.BlockAlign; }
        void Pad(long target) { while(writer.Length<target) writer.Write(zeros,0,(int)Math.Min(zeros.Length,target-writer.Length)); }
        public void Receive(object sender, WaveInEventArgs a) {
            lock(gate) {
                if(closed || a.BytesRecorded==0) return;
                long target=TimeBytes(clock.Elapsed.TotalSeconds)-a.BytesRecorded;
                if (target-writer.Length > writer.WaveFormat.AverageBytesPerSecond*0.18) Pad(target);
                writer.Write(a.Buffer,0,a.BytesRecorded); BytesReceived+=a.BytesRecorded;
                Peak=Measure(a.Buffer,a.BytesRecorded,writer.WaveFormat);
                if(clock.Elapsed.TotalSeconds-lastFlush>=1) { writer.Flush(); lastFlush=clock.Elapsed.TotalSeconds; }
            }
        }
        static float Measure(byte[] bytes,int count,WaveFormat f) {
            float p=0;
            if(f.Encoding==WaveFormatEncoding.IeeeFloat && f.BitsPerSample==32)
                for(int i=0;i+3<count;i+=4) p=Math.Max(p,Math.Abs(BitConverter.ToSingle(bytes,i)));
            else if(f.BitsPerSample==16)
                for(int i=0;i+1<count;i+=2) p=Math.Max(p,Math.Abs(BitConverter.ToInt16(bytes,i)/32768f));
            return Math.Min(1,p);
        }
        public void Finish(double seconds) { lock(gate) { if(closed)return; try { Pad(TimeBytes(seconds)); } finally { closed=true; writer.Dispose(); } } }
        public void Dispose() { Finish(clock.Elapsed.TotalSeconds); }
    }
    public class Recorder {
        readonly List<WasapiCapture> captures=new List<WasapiCapture>();
        readonly List<MMDevice> devices=new List<MMDevice>();
        readonly List<TimedTrack> tracks=new List<TimedTrack>();
        readonly Stopwatch clock=new Stopwatch();
        WasapiOut silence;
        volatile bool stopping;
        public string Fault;
        public double Seconds { get { return clock.Elapsed.TotalSeconds; } }
        public float MicPeak { get { return tracks.Count>0 ? tracks[0].Peak : 0; } }
        public float OutputPeak { get { return tracks.Count>1 ? tracks[1].Peak : 0; } }
        public void Start(Meeting m,string micId,string outputId) {
            try {
                using(var e=new MMDeviceEnumerator()) {
                    var mic=e.GetDevice(micId); devices.Add(mic);
                    Add(new WasapiCapture(mic) { WaveFormat=new WaveFormat(16000,16,1) },Path.Combine(m.Folder,"microphone.wav"));
                    if(m.Mode=="online") {
                        var output=e.GetDevice(outputId); devices.Add(output);
                        var loop=new WasapiLoopbackCapture(output) { WaveFormat=new WaveFormat(16000,16,1) };
                        Add(loop,Path.Combine(m.Folder,"computer.wav"));
                        // Playing zeros keeps WASAPI delivering silent packets continuously.
                        silence=new WasapiOut(output,AudioClientShareMode.Shared,false,100);
                        silence.Init(new SilenceProvider(loop.WaveFormat)); silence.Play();
                    }
                }
                clock.Start();
                foreach(var c in captures)c.StartRecording();
            } catch { Stop(); throw; }
        }
        void Add(WasapiCapture c,string file) {
            captures.Add(c); var track=new TimedTrack(file,c.WaveFormat,clock); tracks.Add(track);
            c.DataAvailable+=track.Receive;
            c.RecordingStopped+=(s,e)=>{ if(!stopping) Fault=e.Exception==null ? "Устройство прекратило запись." : e.Exception.Message; };
        }
        public double Stop() {
            stopping=true;
            foreach(var c in captures) { try { c.StopRecording(); c.Dispose(); } catch(Exception e){Fault=e.Message;} }
            clock.Stop();
            if(silence!=null) { try { silence.Stop(); silence.Dispose(); } catch {} silence=null; }
            foreach(var t in tracks) { try { t.Finish(Seconds); } catch(Exception e) { Fault=e.Message; } }
            foreach(var d in devices)d.Dispose();
            captures.Clear();devices.Clear();
            return Seconds;
        }
    }
    class MonoProvider : ISampleProvider {
        readonly ISampleProvider source; float[] buffer=new float[0];
        public MonoProvider(ISampleProvider s) { source=s; WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(s.WaveFormat.SampleRate,1); }
        public WaveFormat WaveFormat {get;private set;}
        public int Read(float[] b,int offset,int count) {
            int ch=source.WaveFormat.Channels;
            if(buffer.Length<count*ch)buffer=new float[count*ch];
            int n=source.Read(buffer,0,count*ch)/ch;
            for(int i=0;i<n;i++) { float v=0;for(int c=0;c<ch;c++)v+=buffer[i*ch+c];b[offset+i]=v/ch; }
            return n;
        }
    }
    public static class AudioMix {
        public static void Mix(string mic,string computer,string target) {
            string temp=target+".partial.wav";
            using(var a=new WaveFileReader(mic))
            using(var b=File.Exists(computer) ? new WaveFileReader(computer) : null)
            using(var w=new WaveFileWriter(temp,new WaveFormat(16000,16,1))) {
                ISampleProvider sa=new WdlResamplingSampleProvider(new MonoProvider(a.ToSampleProvider()),16000);
                ISampleProvider sb=b==null ? null : new WdlResamplingSampleProvider(new MonoProvider(b.ToSampleProvider()),16000);
                var x=new float[4096];var y=new float[4096];var bytes=new byte[8192];
                while(true) {
                    int na=sa.Read(x,0,x.Length), nb=sb==null ? 0 : sb.Read(y,0,y.Length), n=Math.Max(na,nb);
                    if(n==0)break;
                    for(int i=0;i<n;i++) {
                        float v=(i<na ? x[i] : 0)+(i<nb ? y[i] : 0);
                        // Headroom for simultaneous voices. Original tracks remain intact.
                        if(sb!=null)v*=0.65f;
                        short s=(short)(Math.Max(-1,Math.Min(1,v))*32767);
                        bytes[i*2]=(byte)(s&255);bytes[i*2+1]=(byte)((s>>8)&255);
                    }
                    w.Write(bytes,0,n*2);
                }
            }
            if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
        }
    }
    public static class Summarizer {
        static readonly Regex actions=new Regex(@"\b(нужно|надо|сделать|подготов\w*|отправ\w*|провер\w*|соглас\w*|договор\w*|решил\w*|решени\w*|завтра|пятниц\w*|дедлайн|срок\w*|will|must|need|agreed|decided|deadline|action)\b",RegexOptions.IgnoreCase);
        static readonly HashSet<string> stop=new HashSet<string>(("это как что для или при все был была были будет будут уже есть вот так там тут нас вам они она его еще мне мы вы на по до из не да но то и в с а у к о от за the and that this with from have will for are was not you your our it is to of in a an").Split(' '));
        static string[] Words(string s) {return Regex.Matches(s.ToLowerInvariant(),@"[\p{L}]{3,}").Cast<Match>().Select(m=>m.Value).Where(w=>!stop.Contains(w)).ToArray();}
        public static string Build(string title,string text,string notes) {
            var lines=Regex.Split(Regex.Replace(text,@"\s+"," ").Trim(),@"(?<=[.!?])\s+")
                .Select(x=>x.Trim()).Where(x=>x.Length>8).Distinct().ToList();
            var freq=new Dictionary<string,int>();
            foreach(var w in lines.SelectMany(Words)) { if(!freq.ContainsKey(w))freq[w]=0; freq[w]++; }
            var ranked=lines.Select((s,i)=>new {Text=s,Index=i,Score=Words(s).Distinct().Sum(w=>freq[w]) / Math.Sqrt(Math.Max(1,Words(s).Length))+(actions.IsMatch(s)?2:0)})
                .OrderByDescending(x=>x.Score).Take(5).OrderBy(x=>x.Index).Select(x=>x.Text).ToList();
            var todo=lines.Where(s=>actions.IsMatch(s)).Take(8).ToList();
            var result=new StringBuilder("# "+title+"\n\n");
            result.Append("Локальный краткий итог: ключевые фразы выбраны из расшифровки автоматически. Проверьте формулировки, имена и сроки.\n\n## Основное\n\n");
            if(ranked.Count==0)result.Append("Разборчивая речь не обнаружена. Проверьте аудиозапись.\n");
            foreach(var s in ranked)result.Append("- "+s+"\n");
            result.Append("\n## Возможные договорённости и следующие шаги\n\n");
            if(todo.Count==0)result.Append("Явные формулировки не найдены.\n");
            foreach(var s in todo)result.Append("- "+s+"\n");
            if(!String.IsNullOrWhiteSpace(notes))result.Append("\n## Ваши заметки\n\n"+notes.Trim()+"\n");
            return result.ToString();
        }
    }
    public static class Processor {
        public static bool Available { get { return File.Exists(Path.Combine(Store.Root,"engine","whisper-cli.exe")) && File.Exists(Path.Combine(Store.Root,"models","ggml-base.bin")); } }
        public static string Quote(string s) { return "\""+s+"\""; }
        static string Relative(string path) {
            // whisper-cli's Windows argv path is not UTF-8 on every build.
            // Keep all command-line paths relative to the portable application root.
            return Uri.UnescapeDataString(new Uri(Store.Root).MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('/',Path.DirectorySeparatorChar);
        }
        public static async Task Run(Meeting m,Action<string> progress,CancellationToken token,string provider="openai",string apiKey=null,System.Net.Http.HttpClient apiClient=null) {
            m.Status="processing";m.Error=null;Store.Save(m);
            try {
                progress("Собираю аудио…");
                string audio=Path.Combine(m.Folder,"audio.wav");
                if(File.Exists(Path.Combine(m.Folder,"microphone.wav"))) {
                    await Task.Run(()=>AudioMix.Mix(Path.Combine(m.Folder,"microphone.wav"),Path.Combine(m.Folder,"computer.wav"),audio));
                }
                if(!File.Exists(audio))throw new IOException("Аудиозапись не найдена.");
                token.ThrowIfCancellationRequested();
                string prefix=Path.Combine(m.Folder,"transcript-pending");
                foreach(string extension in new[]{".txt",".srt"})if(File.Exists(prefix+extension))File.Delete(prefix+extension);
                if(provider!="local") {
                    string key=apiKey??ApiKeyStore.Load();
                    var client=apiClient??OpenAiWhisper.CreateClient();
                    try {
                        var result=await OpenAiWhisper.Transcribe(audio,m.Language,key,progress,token,client);
                        token.ThrowIfCancellationRequested();
                        Store.Write(prefix+".txt",result.Text);Store.Write(prefix+".srt",result.Srt);
                        Store.Write(Path.Combine(m.Folder,"recognition.log"),"Provider: OpenAI API\nModel: whisper-1\nResult: complete\n");
                    }finally{if(apiClient==null)client.Dispose();}
                } else {
                if(!Available)throw new IOException("Нет локального движка или модели. Восстановите папки engine и models из дистрибутива.");
                progress("Распознаю речь на компьютере…");
                // Local mode invokes the bundled engine without a shell.
                var start=new ProcessStartInfo(Path.Combine(Store.Root,"engine","whisper-cli.exe")) {
                    Arguments="-m "+Quote("models/ggml-base.bin")+" -f "+Quote(Relative(audio))+" -of "+Quote(Relative(prefix))+
                        " -l "+(new[]{"ru","en","kk","auto"}.Contains(m.Language)?m.Language:"auto")+" -otxt -osrt -ng -t "+Math.Max(1,Math.Min(6,Environment.ProcessorCount-1))+" -pp",
                    WorkingDirectory=Store.Root,UseShellExecute=false,CreateNoWindow=true,
                    RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Store.Utf8,StandardErrorEncoding=Store.Utf8
                };
                string vad=Path.Combine(Store.Root,"models","ggml-silero-v6.2.0.bin");
                if(File.Exists(vad))start.Arguments+=" --vad -vm "+Quote("models/ggml-silero-v6.2.0.bin");
                using(var p=new Process { StartInfo=start }) {
                    p.Start();
                    Task<string> stdout=p.StandardOutput.ReadToEndAsync(),stderr=p.StandardError.ReadToEndAsync();
                    using(token.Register(()=>{try{if(!p.HasExited)p.Kill();}catch{}})) {await Task.Run(()=>p.WaitForExit());}
                    string log=await stderr; string outLog=await stdout;
                    Store.Write(Path.Combine(m.Folder,"recognition.log"),log+"\n"+outLog);
                    token.ThrowIfCancellationRequested();
                    if(p.ExitCode!=0 || !File.Exists(prefix+".txt"))throw new IOException("Распознавание не завершилось. Подробности в recognition.log. Аудио сохранено.");
                }
                }
                token.ThrowIfCancellationRequested();
                progress("Готовлю краткий итог…");
                string transcript=File.ReadAllText(prefix+".txt",Store.Utf8);
                Store.Write(Path.Combine(m.Folder,"transcript.txt"),transcript);
                if(File.Exists(prefix+".srt"))Store.Write(Path.Combine(m.Folder,"transcript.srt"),File.ReadAllText(prefix+".srt",Store.Utf8));
                string noteFile=Path.Combine(m.Folder,"notes.txt");
                string notes=File.Exists(noteFile)?File.ReadAllText(noteFile,Store.Utf8):"";
                Store.Write(Path.Combine(m.Folder,"summary.md"),Summarizer.Build(m.Title,transcript,notes));
                foreach(string extension in new[]{".txt",".srt"})if(File.Exists(prefix+extension))File.Delete(prefix+extension);
                m.TranscriptionModel=provider=="local"?"Whisper base (local)":"whisper-1 (OpenAI API)";
                m.Status="ready";m.Error=null;Store.Save(m); progress("Встреча готова");
            } catch(OperationCanceledException) { m.Status="saved";m.Error="Обработка отменена. Можно запустить её снова.";Store.Save(m);throw; }
            catch(Exception e) {m.Status="failed";m.Error=e.Message;Store.Save(m);throw;}
        }
    }
}
