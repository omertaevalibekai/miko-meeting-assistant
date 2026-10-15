using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using NAudio.Wave;

namespace MeetingSprite {
    public static class ApiTests {
        const string FakeKey="sk-test-fixture-only-not-a-real-api-key";
        static void Check(bool value,string message){if(!value)throw new Exception(message);}
        sealed class FakeServer:HttpMessageHandler {
            public int Calls,Status=200,FailAt=1;
            public bool Network,Timeout,Malformed;
            public string Language="ru";
            public CancellationTokenSource Cancel;
            public long Bytes;
            public readonly List<double> Durations=new List<double>();
            public readonly SHA256 Hash=SHA256.Create();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
                Calls++;Check(request.Method==HttpMethod.Post&&request.RequestUri.AbsoluteUri=="https://api.openai.com/v1/audio/transcriptions","Wrong endpoint");
                Check(request.Headers.Authorization.Scheme=="Bearer"&&request.Headers.Authorization.Parameter==FakeKey,"Wrong authorization");
                var form=request.Content as MultipartFormDataContent;Check(form!=null,"Expected multipart");var fields=new Dictionary<string,string>();byte[] wav=null;
                foreach(var field in form) {
                    string name=field.Headers.ContentDisposition.Name.Trim('"');
                    if(name=="file") {Check(field.Headers.ContentType.MediaType=="audio/wav","MIME");wav=await field.ReadAsByteArrayAsync();}
                    else fields[name]=await field.ReadAsStringAsync();
                }
                Check(fields["model"]=="whisper-1"&&fields["response_format"]=="verbose_json"&&fields["timestamp_granularities[]"]=="segment","Wrong API fields");
                Check(Language=="auto"?!fields.ContainsKey("language"):fields["language"]==Language,"Wrong language field");
                Check(wav!=null&&wav.Length<25000000,"API upload size limit");
                using(var stream=new MemoryStream(wav))using(var reader=new WaveFileReader(stream)) {
                    Check(reader.WaveFormat.SampleRate==16000&&reader.WaveFormat.BitsPerSample==16&&reader.WaveFormat.Channels==1,"Invalid WAV header");
                    Durations.Add(reader.TotalTime.TotalSeconds);var buffer=new byte[65536];int n;
                    while((n=reader.Read(buffer,0,buffer.Length))>0){Hash.TransformBlock(buffer,0,n,buffer,0);Bytes+=n;}
                }
                if(Cancel!=null){Cancel.Cancel();token.ThrowIfCancellationRequested();}
                if(Network)throw new HttpRequestException("private upstream error "+FakeKey);
                if(Timeout)throw new TaskCanceledException();
                if(Calls==FailAt&&Status!=200)return new HttpResponseMessage((HttpStatusCode)Status){Content=new StringContent("untrusted server error "+FakeKey)};
                string json=Malformed?"{invalid}":"{\"text\":\"Нужно проверить план.\",\"segments\":[{\"start\":0.25,\"end\":1.0,\"text\":\"Нужно проверить план.\"}]}";
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
            }
        }
        static void Audio(string path,int seconds) {
            using(var writer=new WaveFileWriter(path,new WaveFormat(16000,16,1))) {
                byte[] data=new byte[32000];for(int i=0;i<data.Length;i+=2){short value=(short)(8000*Math.Sin(i*0.07));data[i]=(byte)value;data[i+1]=(byte)(value>>8);}
                for(int i=0;i<seconds;i++)writer.Write(data,0,data.Length);
            }
        }
        static Meeting MeetingAt(string root,string name,string audio) {
            string folder=Path.Combine(root,name);Directory.CreateDirectory(folder);File.Copy(audio,Path.Combine(folder,"audio.wav"),true);
            return new Meeting{Folder=folder,Title="Проверка API",Language="ru",Status="saved",Mode="room"};
        }
        static async Task RunAll(string folder,StringBuilder log) {
            string shortAudio=Path.Combine(folder,"short.wav"),longAudio=Path.Combine(folder,"long.wav");Audio(shortAudio,2);Audio(longAudio,800);
            string vault=Path.Combine(folder,"fixture-key.dpapi");ApiKeyStore.Save(FakeKey,vault);Check(ApiKeyStore.Load(vault)==FakeKey,"DPAPI roundtrip");Check(!File.ReadAllText(vault).Contains(FakeKey),"Plain key on disk");File.Delete(vault);
            log.AppendLine("PASS: Windows DPAPI encrypts the key for the current user; roundtrip succeeds; no plaintext key file.");
            using(var server=new FakeServer())using(var client=new HttpClient(server)) {
                var result=await OpenAiWhisper.Transcribe(longAudio,"ru",FakeKey,s=>{},CancellationToken.None,client);
                server.Hash.TransformFinalBlock(new byte[0],0,0);byte[] expected;
                using(var reader=new WaveFileReader(longAudio))using(var hash=SHA256.Create()){var b=new byte[65536];int n;while((n=reader.Read(b,0,b.Length))>0)hash.TransformBlock(b,0,n,b,0);hash.TransformFinalBlock(new byte[0],0,0);expected=hash.Hash;}
                Check(server.Calls==2&&server.Bytes==800*32000&&server.Hash.Hash.SequenceEqual(expected),"Chunking lost, duplicated or reordered samples");
                string second=TimeSpan.FromSeconds(server.Durations[0]+0.25).ToString(@"hh\:mm\:ss\,fff");Check(result.Srt.Contains(second+" -->")&&result.Srt.Contains("\n2\r\n"),"SRT timestamps did not advance across chunks");
                Check(result.Text.Split(new[]{"Нужно"},StringSplitOptions.None).Length==3,"Chunk transcript lost");
            }
            log.AppendLine("PASS: >25 MB recording split into valid WAV uploads below 25 MB; exact PCM SHA256 retained across boundaries; SRT uses cumulative timestamps.");
            using(var server=new FakeServer{Language="auto"})using(var client=new HttpClient(server)) {
                var m=MeetingAt(folder,"success",shortAudio);m.Language="auto";
                await Processor.Run(m,s=>{},CancellationToken.None,"openai",FakeKey,client);
                Check(m.Status=="ready"&&m.TranscriptionModel=="whisper-1 (OpenAI API)"&&File.ReadAllText(Path.Combine(m.Folder,"transcript.txt")).Contains("Нужно проверить план."),"Successful API result or model metadata lost");
                Check(File.Exists(Path.Combine(m.Folder,"summary.md"))&&File.Exists(Path.Combine(m.Folder,"transcript.srt")),"Artifacts missing");
                Check(!Directory.GetFiles(m.Folder).Where(p=>!p.EndsWith(".wav")).Any(p=>File.ReadAllText(p).Contains(FakeKey)),"Key persisted in result or log");
            }
            log.AppendLine("PASS: auto language omitted; mocked API success produces TXT, SRT and extractive summary without logging keys.");
            foreach(string name in new[]{"unauthorized","quota","network","timeout","malformed","partial"}) {
                using(var server=new FakeServer{Status=name=="unauthorized"?401:name=="quota"||name=="partial"?429:200,FailAt=name=="partial"?2:1,Network=name=="network",Timeout=name=="timeout",Malformed=name=="malformed"})
                using(var client=new HttpClient(server)) {
                    var m=MeetingAt(folder,name,name=="partial"?longAudio:shortAudio);Store.Write(Path.Combine(m.Folder,"transcript.txt"),"PREVIOUS RESULT");bool failed=false;
                    try{await Processor.Run(m,s=>{},CancellationToken.None,"openai",FakeKey,client);}catch(IOException ex){failed=true;Check(!ex.Message.Contains(FakeKey),"Raw server error leaked a key");}
                    Check(failed&&m.Status=="failed"&&File.Exists(Path.Combine(m.Folder,"audio.wav")),"Failure lost audio or state");
                    Check(File.ReadAllText(Path.Combine(m.Folder,"transcript.txt"))=="PREVIOUS RESULT","Failed run overwrote prior result");
                    Check(server.Calls==(name=="partial"?2:1),"Unexpected automatic retry");
                }
            }
            log.AppendLine("PASS: 401, 429, offline, timeout, invalid response and failure of a later chunk retain audio/prior transcript; no automatic paid retries; error bodies not leaked.");
            using(var cancel=new CancellationTokenSource())using(var server=new FakeServer{Cancel=cancel})using(var client=new HttpClient(server)) {
                var m=MeetingAt(folder,"cancel",shortAudio);bool stopped=false;
                try{await Processor.Run(m,s=>{},cancel.Token,"openai",FakeKey,client);}catch(OperationCanceledException){stopped=true;}
                Check(stopped&&m.Status=="saved"&&File.Exists(Path.Combine(m.Folder,"audio.wav")),"In-flight cancellation failed");
            }
            using(var server=new FakeServer())using(var client=new HttpClient(server)) {
                var m=MeetingAt(folder,"missing-key",shortAudio);File.Move(Path.Combine(m.Folder,"audio.wav"),Path.Combine(m.Folder,"microphone.wav"));bool failed=false;
                try{await Processor.Run(m,s=>{},CancellationToken.None,"openai","",client);}catch(InvalidOperationException){failed=true;}
                Check(failed&&server.Calls==0&&File.Exists(Path.Combine(m.Folder,"audio.wav")),"Missing key should still preserve mixed playable audio without requests");
            }
            log.AppendLine("PASS: in-flight cancellation leaves saved audio; missing API key still creates playable mixed WAV and sends no request.");
            log.AppendLine("No real API request, user recording or microphone was used. Live account authentication remains unverified.");
        }
        public static int Run(string folder) {
            Directory.CreateDirectory(folder);var log=new StringBuilder();
            try{RunAll(folder,log).GetAwaiter().GetResult();Store.Write(Path.Combine(folder,"api-results.txt"),log.ToString());return 0;}
            catch(Exception ex){Store.Write(Path.Combine(folder,"api-results.txt"),log+"FAIL: "+ex);return 1;}
        }
    }
}
