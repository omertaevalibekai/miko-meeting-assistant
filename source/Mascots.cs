using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MeetingSprite {
    public class PixelMascot : FrameworkElement {
        public static readonly string[] Names={"Мико","Луми","Пикс","Орби","Бип"};
        public static readonly string[] Descriptions={"Робот с экраном","Тихое привидение","Любопытный лисёнок","Маленький космонавт","Кассетный друг"};
        public int Kind;
        public string State="idle";
        public float Level;
        public double Phase;
        public bool Animate=true;
        public bool Hover;
        double look;
        DrawingContext dc;
        static readonly Dictionary<string,Brush> brushes=new Dictionary<string,Brush>();
        const string Edge="#232332";
        public PixelMascot() {Width=114;Height=144;SnapsToDevicePixels=true;RenderOptions.SetEdgeMode(this,EdgeMode.Aliased);}
        public static Brush Ink(string color) {
            Brush value;
            if(!brushes.TryGetValue(color,out value)){value=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));value.Freeze();brushes[color]=value;}
            return value;
        }
        protected override void OnMouseMove(MouseEventArgs e) {base.OnMouseMove(e);look=Math.Max(-1,Math.Min(1,(e.GetPosition(this).X-Width/2)/28));}
        void R(double x,double y,double w,double h,string color) {dc.DrawRectangle(Ink(color),null,new Rect(x*3,y*3,w*3,h*3));}
        double Beat {get{return Animate?Math.Sin(Phase*2.2):0;}}
        void Eyes(int left,int top,int right,string color) {
            bool blink=Animate && (Phase+Kind*0.17)%4.6<0.15;
            int h=blink?1:4, dx=Hover?(int)Math.Round(look):0;
            if(State=="processing")h=1;
            R(left+dx,top+(4-h)/2,3,h,color);R(right+dx,top+(4-h)/2,3,h,color);
            if(h>1){R(left+dx,top,1,1,"#F4FFF9");R(right+dx,top,1,1,"#F4FFF9");}
        }
        void FaceSignal(int x,int y) {
            if(State=="recording") {
                for(int i=0;i<5;i++){int h=1+(int)(Level*5*Math.Abs(Math.Sin(i*1.3+Phase*4)));R(x+i*2,y-h,1,h,"#A5DBC7");}
                R(x-3,y-2,2,2,"#F27B7C");
            } else if(State=="processing") {
                for(int i=0;i<3;i++)R(x+i*3,y-(Animate&&((int)(Phase*3)%3)==i?1:0),2,1,"#D3BFF3");
            } else R(x+2,y,4,1,"#B7A8CD");
        }
        protected override void OnRender(DrawingContext drawing) {
            dc=drawing;double bob=Animate?Math.Sin(Phase*2.2)*1.8:0;
            if(State=="recording")bob-=Level*2;
            dc.PushTransform(new TranslateTransform(3,bob+2));
            switch(Math.Max(0,Math.Min(4,Kind))) {case 1:Ghost();break;case 2:Fox();break;case 3:Astronaut();break;case 4:Cassette();break;default:Robot();break;}
            if(State=="recording") {R(28,29,3,3,Edge);R(29,30,1,1,"#F27B7C");}
            dc.Pop();dc=null;
        }
        void Robot() {
            R(17,2,2,7,Edge);R(18,2,6,2,Edge);R(23,1,3,3,Edge);R(24,1,1,1,"#C1A6EA");
            R(8,8,20,2,Edge);R(6,10,24,17,Edge);R(8,27,20,2,Edge);
            R(8,10,20,16,"#777084");R(7,11,1,14,"#777084");R(8,10,19,1,"#ADA1BA");R(8,11,1,14,"#ADA1BA");
            R(10,12,16,12,Edge);R(11,13,14,10,"#28333D");R(11,13,14,1,"#3A4650");
            R(28,12,1,13,"#4B455C");R(9,26,18,1,"#4B455C");
            R(12,29,12,10,Edge);R(13,29,10,9,"#595064");R(14,30,8,1,"#90829F");
            int hand=Hover&&Animate?(int)Math.Round(Beat):0;
            R(8,30,4,7,Edge);R(9,31,2,4,"#8D7E9C");R(24,30+hand,4,7,Edge);R(25,31+hand,2,4,"#8D7E9C");
            R(12,39,5,4,Edge);R(19,39,5,4,Edge);R(13,39,3,2,"#777084");R(20,39,3,2,"#777084");
            R(15,32,6,5,Edge);R(16,33,4,3,"#9381AB");R(17,33,2,1,"#CEB5EC");R(16,34,2,1,"#CEB5EC");
            Eyes(12,16,21,"#A4DDCA");FaceSignal(15,22);
        }
        void Ghost() {
            R(13,8,12,2,Edge);R(10,10,18,3,Edge);R(8,13,22,5,Edge);R(6,18,26,16,Edge);
            R(7,34,5,5,Edge);R(14,34,5,7,Edge);R(21,34,5,5,Edge);R(28,33,4,4,Edge);
            R(13,10,12,2,"#DEF1DF");R(10,12,18,5,"#DEF1DF");R(8,17,22,16,"#DEF1DF");
            R(7,21,1,12,"#DEF1DF");R(8,33,3,4,"#DEF1DF");R(15,33,3,6,"#DEF1DF");R(22,33,3,4,"#DEF1DF");R(29,28,2,7,"#A5CABD");
            R(12,13,3,3,"#FAFDED");R(9,17,2,7,"#FAFDED");R(26,17,3,16,"#B3D7C6");
            R(5,24,4,5,Edge);R(6,25,3,3,"#C6E3D2");R(29,23,5,5,Edge);R(30,24,3,3,"#C6E3D2");
            Eyes(12,20,22,"#384F52");R(10,25,3,1,"#F0B4B1");R(25,25,3,1,"#F0B4B1");
            if(State=="idle"){R(17,27,4,2,"#526968");R(18,29,2,1,"#526968");}else FaceSignal(15,30);
            R(17,6,2,2,"#88BFAE");R(19,4,3,2,"#88BFAE");R(22,3,2,2,"#C2E3C9");
        }
        void Fox() {
            int tail=Animate?(int)Math.Round(Beat):0;
            R(25,25+tail,8,9,Edge);R(30,21+tail,5,10,Edge);R(31,18+tail,4,5,Edge);
            R(27,26+tail,5,6,"#D68B57");R(31,23+tail,3,7,"#EBA66F");R(32,19+tail,2,5,"#FFF0CF");
            R(6,5,4,12,Edge);R(10,8,3,9,Edge);R(25,5,4,12,Edge);R(22,8,3,9,Edge);
            R(7,7,2,8,"#EBA66F");R(9,10,2,6,"#E6AC9E");R(26,7,2,8,"#EBA66F");R(24,10,2,6,"#E6AC9E");
            R(9,13,17,3,Edge);R(6,16,23,11,Edge);R(9,27,17,4,Edge);
            R(10,15,15,3,"#EBA66F");R(7,18,21,8,"#EBA66F");R(10,26,15,3,"#FFF0CF");
            R(8,22,4,4,"#FFF0CF");R(23,22,4,4,"#FFF0CF");R(11,25,13,4,"#FFF0CF");
            R(12,31,12,10,Edge);R(13,31,10,8,"#D98D5C");R(15,31,6,7,"#FFF0CF");
            R(10,39,6,4,Edge);R(20,39,6,4,Edge);R(11,40,4,1,"#8F5D54");R(21,40,4,1,"#8F5D54");
            Eyes(11,19,21,"#433C40");R(16,24,4,2,Edge);R(17,26,2,1,Edge);
            if(State!="idle")FaceSignal(14,36);
        }
        void Astronaut() {
            R(11,6,15,2,Edge);R(8,8,21,3,Edge);R(6,11,25,15,Edge);R(9,26,19,3,Edge);
            R(11,8,15,2,"#D7E4F6");R(8,11,21,14,"#90B5E8");R(7,13,1,10,"#90B5E8");R(10,9,17,2,"#D7E4F6");
            R(10,12,17,12,Edge);R(11,13,15,10,"#32465D");R(12,14,3,2,"#566C86");
            R(3,15,4,9,Edge);R(4,16,2,6,"#D7E4F6");R(31,15,4,9,Edge);R(32,16,2,6,"#90B5E8");
            R(12,29,13,11,Edge);R(13,29,11,10,"#BCD0EE");R(14,31,8,5,"#607EA8");R(16,32,2,2,"#E6C873");R(20,32,1,2,"#C4DEDC");
            R(8,30,4,8,Edge);R(9,31,2,5,"#90B5E8");R(25,30,4,8,Edge);R(26,31,2,5,"#90B5E8");
            R(12,40,5,3,Edge);R(20,40,5,3,Edge);R(13,40,3,1,"#E0E8F2");R(21,40,3,1,"#E0E8F2");
            Eyes(13,16,21,"#F6DB95");FaceSignal(15,22);
            R(28,3,1,5,"#E7C76C");R(26,5,5,1,"#E7C76C");
        }
        void Cassette() {
            R(7,12,23,2,Edge);R(5,14,27,19,Edge);R(7,33,23,2,Edge);
            R(7,14,23,18,"#E7C76C");R(6,16,1,14,"#E7C76C");R(8,14,21,1,"#FFF0A6");R(29,16,2,15,"#BD934F");
            R(9,17,19,10,Edge);R(10,18,17,8,"#5C5250");R(11,19,5,5,"#F5DEAA");R(21,19,5,5,"#F5DEAA");
            bool blink=Animate&&Phase%4.6<0.15;
            if(blink){R(12,21,3,1,Edge);R(22,21,3,1,Edge);}else {
                int spin=Animate?((int)(Phase*(State=="recording"?8:2))%2):0;
                R(13,20,1,3,Edge);R(12,21,3,1,Edge);R(23,20,1,3,Edge);R(22,21,3,1,Edge);
                if(spin==1){R(12,20,1,1,"#7F6D5D");R(14,22,1,1,"#7F6D5D");R(22,20,1,1,"#7F6D5D");R(24,22,1,1,"#7F6D5D");}
            }
            R(10,29,17,2,"#FFF0BE");R(12,28,13,1,"#FFF0BE");FaceSignal(14,30);
            R(8,9,7,3,Edge);R(9,10,5,1,"#E7C76C");R(23,9,6,3,Edge);R(24,10,4,1,"#E7C76C");
            R(2,21,3,9,Edge);R(3,22,1,5,"#E7C76C");R(32,21,3,9,Edge);R(33,22,1,5,"#E7C76C");
            R(11,35,5,7,Edge);R(22,35,5,7,Edge);R(12,35,3,5,"#B59A6D");R(23,35,3,5,"#B59A6D");R(10,41,7,2,Edge);R(21,41,7,2,Edge);
        }
    }
}
