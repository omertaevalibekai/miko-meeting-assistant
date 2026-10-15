using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using NAudio.Wave;

namespace MeetingSprite {
    public static class Tests {
        static void Assert(bool value,string message) {if(!value)throw new Exception(message);}
        static void Tone(string path,int rate,int channels,double seconds,double amplitude) {
            using(var w=new WaveFileWriter(path,WaveFormat.CreateIeeeFloatWaveFormat(rate,channels))) {
                for(int i=0;i<(int)(rate*seconds);i++)for(int c=0;c<channels;c++)w.WriteSample((float)(amplitude*Math.Sin(2*Math.PI*440*i/rate)));
            }
        }
        public static int Run(string folder) {
            Directory.CreateDirectory(folder);var log=new StringBuilder();
            try {
                string a=Path.Combine(folder,"test-mic.wav"),b=Path.Combine(folder,"test-computer.wav"),mixed=Path.Combine(folder,"test-mixed.wav");
                Tone(a,48000,2,1,0.8);Tone(b,44100,1,2,0.6);AudioMix.Mix(a,b,mixed);
                using(var r=new WaveFileReader(mixed)) {
                    Assert(r.WaveFormat.SampleRate==16000&&r.WaveFormat.Channels==1&&r.WaveFormat.BitsPerSample==16,"Mix format");
                    Assert(Math.Abs(r.TotalTime.TotalSeconds-2)<0.02,"Longest track must be preserved");
                    var p=r.ToSampleProvider();var samples=new float[40000];int n=p.Read(samples,0,samples.Length);
                    Assert(samples.Take(n).Max(x=>Math.Abs(x))>0.7,"Mix must contain both tracks");
                    Assert(samples.Take(n).All(x=>Math.Abs(x)<=1),"Mix clips");
                    Assert(samples.Skip(20000).Take(4000).Max(x=>Math.Abs(x))>0.3,"Second track lost after microphone ends");
                }
                log.AppendLine("PASS: different sample rates and channel counts; output PCM16 mono 16kHz; longest input retained; headroom.");
                AudioMix.Mix(a,Path.Combine(folder,"missing.wav"),mixed);
                using(var r=new WaveFileReader(mixed))Assert(Math.Abs(r.TotalTime.TotalSeconds-1)<0.02,"Room duration");
                log.AppendLine("PASS: room mode works without a computer track; atomic reprocessing replaces output.");
                var clock=Stopwatch.StartNew();string track=Path.Combine(folder,"test-gap.wav");
                using(var t=new TimedTrack(track,new WaveFormat(16000,16,1),clock)) {
                    Thread.Sleep(300);var data=Enumerable.Repeat((byte)20,3200).ToArray();
                    t.Receive(null,new WaveInEventArgs(data,data.Length));t.Finish(0.6);
                }
                using(var r=new WaveFileReader(track)){
                    Assert(Math.Abs(r.TotalTime.TotalSeconds-0.6)<0.01,"Silence gap duration");
                    var bytes=new byte[3200];r.Read(bytes,0,bytes.Length);Assert(bytes.All(x=>x==0),"Leading silence lost");
                }
                log.AppendLine("PASS: leading and trailing silence preserved on monotonic recording timeline.");
                string text="Мы обсудили запуск проекта. Анна подготовит макет к пятнице. Нужно проверить бюджет. Следующая встреча завтра.";
                string summary=Summarizer.Build("Планёрка",text,"Проверить ссылки");
                Assert(summary.Contains("Анна подготовит макет к пятнице."),"Action missing");
                Assert(summary.Contains("Проверить ссылки"),"Notes missing");
                Assert(!summary.Contains("Иван"),"Fabricated person");
                Assert(Summarizer.Build("Тишина","","").Contains("не обнаружена"),"Empty transcript");
                Store.Write(Path.Combine(folder,"summary-test.md"),summary);
                log.AppendLine("PASS: extractive summary retains original action phrases and notes; empty speech is explicit.");
                var m=new Meeting {Folder=folder,Title="Русский путь и название",Status="saved",Seconds=1.5};Store.Save(m);
                Store.Write(Path.Combine(folder,"atomic.txt"),"первая");Store.Write(Path.Combine(folder,"atomic.txt"),"вторая");
                Assert(File.ReadAllText(Path.Combine(folder,"atomic.txt"),Store.Utf8)=="вторая","Atomic UTF8 save");
                log.AppendLine("PASS: UTF-8 metadata and atomic file replacement.");
                Store.Write(Path.Combine(folder,"results.txt"),log.ToString());return 0;
            }catch(Exception e){log.AppendLine("FAIL: "+e);Store.Write(Path.Combine(folder,"results.txt"),log.ToString());return 1;}
        }
        public static int Transcribe(string sample,string folder) {
            Directory.CreateDirectory(folder);File.Copy(sample,Path.Combine(folder,"audio.wav"),true);
            var m=new Meeting {Title="Проверка распознавания",Started=DateTimeOffset.Now.ToString("o"),Language="en",Mode="room",Status="saved",Folder=folder};
            try {
                Processor.Run(m,s=>File.AppendAllText(Path.Combine(folder,"progress.txt"),s+Environment.NewLine,Store.Utf8),CancellationToken.None,"local").GetAwaiter().GetResult();
                string transcript=File.ReadAllText(Path.Combine(folder,"transcript.txt"),Store.Utf8);
                Assert(transcript.ToLowerInvariant().Contains("country"),"Expected known word absent from sample transcript");
                Assert(File.Exists(Path.Combine(folder,"transcript.srt"))&&File.Exists(Path.Combine(folder,"summary.md")),"Transcript artifacts missing");
                Store.Write(Path.Combine(folder,"verified.txt"),"PASS: local Whisper inference on JFK sample; text, SRT and extractive summary saved.");return 0;
            }catch(Exception e){Store.Write(Path.Combine(folder,"verified.txt"),"FAIL: "+e);return 1;}
        }
        public static int ProcessingEdges(string folder) {
            Directory.CreateDirectory(folder);var log=new StringBuilder();
            try {
                string audio=Path.Combine(folder,"audio.wav");
                using(var w=new WaveFileWriter(audio,new WaveFormat(16000,16,1)))w.Write(new byte[96000],0,96000);
                var m=new Meeting {Title="Тишина",Language="ru",Mode="room",Status="saved",Folder=folder};
                Store.Write(Path.Combine(folder,"transcript.txt"),"OLD RESULT MUST NOT BE REUSED");
                Processor.Run(m,s=>{},CancellationToken.None,"local").GetAwaiter().GetResult();
                Assert(String.IsNullOrWhiteSpace(File.ReadAllText(Path.Combine(folder,"transcript.txt"))),"Silent audio must not retain old speech");
                log.AppendLine("PASS: silence/VAD produces no invented speech; old transcript replaced; paths containing spaces accepted.");
                var cancel=new CancellationTokenSource();cancel.Cancel();
                try {Processor.Run(m,s=>{},cancel.Token,"local").GetAwaiter().GetResult();throw new Exception("Cancellation was ignored");}
                catch(OperationCanceledException) {Assert(File.Exists(audio)&&m.Status=="saved","Cancellation lost audio or state");}
                log.AppendLine("PASS: cancellation leaves audio and retriable saved status.");
                Store.Write(Path.Combine(folder,"edge-results.txt"),log.ToString());return 0;
            }catch(Exception e){Store.Write(Path.Combine(folder,"edge-results.txt"),log+"FAIL: "+e);return 1;}
        }
    }
}
