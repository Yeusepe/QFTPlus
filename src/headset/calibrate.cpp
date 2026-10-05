#include <opencv2/core.hpp>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <stdexcept>
#include <vector>

namespace {
struct Input {
    std::ifstream file;
    explicit Input(const std::string &path) : file(path, std::ios::binary) {}
    void read(void *p, size_t n) { if (!file.read(static_cast<char *>(p), n)) throw std::runtime_error("Incomplete calibration data"); }
    void magic(const char *expected) { char m[8]; read(m,8); if (std::memcmp(m,expected,8)) throw std::runtime_error("Unsupported calibration data"); }
    cv::Mat matrix(int rows,int cols) {
        cv::Mat m(rows,cols,CV_32F); read(m.data,m.total()*4);
        if (!cv::checkRange(m)) throw std::runtime_error("Invalid calibration numbers"); return m;
    }
    void end() { if (file.peek()!=EOF) throw std::runtime_error("Unexpected calibration data"); }
};
struct Head {
    cv::Mat missing,w1,b1,w2,b2,w3,b3;
    Head(Input &in,bool brow) {
        missing=in.matrix(brow?1:6,brow?480:512);w1=in.matrix(brow?128:512,brow?961:4102);b1=in.matrix(w1.rows,1);
        w2=in.matrix(brow?8:256,w1.rows);b2=in.matrix(w2.rows,1);
        if(!brow){w3=in.matrix(4,256);b3=in.matrix(4,1);}
    }
};
cv::Mat layer(const cv::Mat &w,const cv::Mat &b,const cv::Mat &x,bool sigmoid) {
    cv::Mat y=w*x+b;
    for(int i=0;i<y.rows;i++){float v=y.at<float>(i),s=1.f/(1.f+std::exp(-std::clamp(v,-60.f,60.f)));y.at<float>(i)=sigmoid?s:v*s;}return y;
}
cv::Mat mean(const cv::Mat &m) { cv::Mat out;cv::reduce(m,out,0,cv::REDUCE_AVG,CV_32F);return out; }
float median(std::vector<float> v) { std::sort(v.begin(),v.end());size_t m=v.size()/2;return v.size()%2?v[m]:(v[m-1]+v[m])*.5f; }
float percentile(std::vector<float> v,double q) { std::sort(v.begin(),v.end());double at=q*(v.size()-1);size_t i=static_cast<size_t>(at),j=std::min(i+1,v.size()-1);return static_cast<float>(v[i]+(v[j]-v[i])*(at-i)); }
struct Output {
    std::vector<float> data;
    void add(const cv::Mat &m) { cv::Mat c=m.isContinuous()?m:m.clone();auto p=c.ptr<float>();data.insert(data.end(),p,p+c.total()); }
    void zeros(size_t n) { data.insert(data.end(),n,0); }
    void write(const std::string &path,uint32_t capabilities) {
        for(float x:data)if(!std::isfinite(x))throw std::runtime_error("Non-finite calibration result");
        std::ofstream out(path,std::ios::binary|std::ios::trunc);uint32_t bytes=static_cast<uint32_t>(data.size()*4);
        out.write("QFTHP002",8);out.write(reinterpret_cast<char*>(&capabilities),4);out.write(reinterpret_cast<char*>(&bytes),4);
        out.write(reinterpret_cast<char*>(data.data()),bytes);out.close();if(!out)throw std::runtime_error("Could not save calibration");
    }
};
void compile(const std::string &dir) {
    Input input(dir+"/heads.bin");input.magic("QFTHD001");Head mouth(input,false),brows(input,true);input.end();
    Input capture(dir+"/calibration.bin");capture.magic("QFCAL002");std::array<uint32_t,12> counts{};capture.read(counts.data(),48);
    std::array<cv::Mat,12> holds;
    for(int i=0;i<12;i++){if(counts[i]<8||counts[i]>120)throw std::runtime_error("Invalid hold length");holds[i]=capture.matrix(counts[i],1511);}capture.end();
    const int slot[6]={0,1,2,3,6,11};cv::Mat anchors(6,512,CV_32F);
    for(int i=0;i<6;i++)mean(holds[slot[i]].colRange(0,512)).copyTo(anchors.row(i));
    cv::Mat neutral=mean(holds[0].colRange(519,999));
    cv::Mat mw=mouth.w1.colRange(0,512)+mouth.w1.colRange(3584,4096);
    cv::Mat mb=mouth.b1+mouth.w1.colRange(512,3584)*anchors.reshape(1,3072)-mouth.w1.colRange(3584,4096)*anchors.row(0).t()+mouth.w1.colRange(4096,4102)*cv::Mat::ones(6,1,CV_32F);
    cv::Mat bw=brows.w1.colRange(0,480)+brows.w1.colRange(480,960);
    cv::Mat bb=brows.b1-brows.w1.colRange(480,960)*neutral.t()+brows.w1.col(960);
    Output out;out.add(mw);out.add(mb);out.add(mouth.w2);out.add(mouth.b2);out.add(mouth.w3);out.add(mouth.b3);out.add(bw);out.add(bb);out.add(brows.w2);out.add(brows.b2);
    int n=0;for(int i=6;i<=10;i++)n+=counts[i]*2;
    cv::Mat x(n,512,CV_64F),y(n,2,CV_64F),mu,variance;
    const double directions[5][2]={{0,0},{0,1},{0,-1},{-1,0},{1,0}};
    int row=0;
    for(int i=6;i<=10;i++)for(int copy=0;copy<2;copy++)for(int j=0;j<holds[i].rows;j++,row++){
        holds[i].row(j).colRange(0,512).convertTo(x.row(row),CV_64F);y.at<double>(row,0)=directions[i-6][0];y.at<double>(row,1)=directions[i-6][1];
    }
    cv::reduce(x,mu,0,cv::REDUCE_AVG,CV_64F);cv::Mat xn=x-cv::repeat(mu,n,1);
    cv::reduce(xn.mul(xn),variance,0,cv::REDUCE_AVG,CV_64F);cv::sqrt(variance,variance);variance+=1e-3;
    cv::divide(xn,cv::repeat(variance,n,1),xn);
    cv::Mat solved;cv::solve(xn*xn.t()+4096*cv::Mat::eye(n,n,CV_64F),y,solved,cv::DECOMP_CHOLESKY);
    if(solved.empty())throw std::runtime_error("Tongue calibration could not be fitted");
    cv::Mat weights=xn.t()*solved,mapping(2,512,CV_32F);
    cv::Mat wf,mf,sf;weights.convertTo(wf,CV_32F);mu.convertTo(mf,CV_32F);variance.convertTo(sf,CV_32F);
    for(int i=0;i<2;i++)for(int j=0;j<512;j++)mapping.at<float>(i,j)=wf.at<float>(j,i)/sf.at<float>(j);
    cv::Mat gains(4,1,CV_32F);const int gainSlots[4]={9,10,8,7},axes[4]={0,0,1,1};
    for(int i=0;i<4;i++) {
        std::vector<float> values;
        for(int j=0;j<holds[gainSlots[i]].rows;j++){
            cv::Mat q;holds[gainSlots[i]].row(j).colRange(0,512).convertTo(q,CV_64F);
            cv::divide(q-mu,variance,q);cv::Mat predicted=q*weights;values.push_back(static_cast<float>(predicted.at<double>(axes[i])));
        }
        float reach=median(values)*(i%2?1:-1);gains.at<float>(i)=reach>=.15f?std::min(1.f/reach,2.f):1;
    }
    out.add(mapping);out.add(-mapping*mf.t());out.add(gains);
    cv::Mat rest=cv::Mat::zeros(13,1,CV_32F),reach=cv::Mat::ones(13,1,CV_32F);
    auto cheek=[&](int slotIndex){return layer(mouth.w3,mouth.b3,layer(mouth.w2,mouth.b2,layer(mw,mb,anchors.row(slotIndex).t(),false),false),true);};
    cv::Mat base=cheek(0);
    for(int family=0;family<2;family++){
        float low=std::max(base.at<float>(2*family),base.at<float>(2*family+1));cv::Mat full=cheek(family?5:3);float high=std::max(full.at<float>(2*family),full.at<float>(2*family+1));
        for(int side=0;side<2;side++){rest.at<float>(2*family+side)=low;if(high-low>=.2f)reach.at<float>(2*family+side)=high;}
    }
    auto thumbs=[&](int i){return holds[i].colRange(999,1511);};
    auto part=[](const cv::Mat &m,int c){return m.colRange(c*256,c*256+256);};
    cv::Mat still=mean(thumbs(0)),direction(2,256,CV_32F);float camera[2],threshold[2],amount[2]={rest.at<float>(0),reach.at<float>(0)};
    for(int side=0;side<2;side++){
        cv::Mat own=mean(thumbs(4+side))-still,other=mean(thumbs(5-side))-still,on;
        auto moved=[&](int c){return cv::norm(part(own,c),cv::NORM_L1)-cv::norm(part(other,c),cv::NORM_L1);};
        int c=moved(1)>moved(0);camera[side]=static_cast<float>(c);
        cv::vconcat(thumbs(4+side),thumbs(3),on);
        cv::Mat d=part(mean(on),c)-part(still,c);cv::Mat(d/std::max(d.dot(d),1e-9)).copyTo(direction.row(side));
        std::vector<float> off;
        for(int i=0;i<12;i++)if(i!=3&&i!=4+side)for(int j=0;j<holds[i].rows;j++)
            off.push_back(std::clamp(static_cast<float>((part(thumbs(i).row(j),c)-part(still,c)).dot(direction.row(side))),0.f,1.f));
        threshold[side]=std::clamp(percentile(off,.99)+.1f,.15f,.5f);
        rest.at<float>(side)=0;reach.at<float>(side)=1;
    }
    uint32_t capabilities=3;
    out.add(still);out.add(direction);out.add(cv::Mat(1,2,CV_32F,camera));out.add(cv::Mat(1,2,CV_32F,threshold));out.add(cv::Mat(1,2,CV_32F,amount));
    std::array<std::vector<float>,4> browRest;
    for(int j=0;j<holds[0].rows;j++){
        cv::Mat result=layer(brows.w2,brows.b2,layer(bw,bb,holds[0].row(j).colRange(519,999).t(),false),true);
        for(int i=0;i<4;i++)browRest[i].push_back(result.at<float>(4+i));
    }
    for(int i=0;i<4;i++)rest.at<float>(8+i)=median(browRest[i]);
    out.add(rest);out.add(reach);out.zeros(1);
    Input previous(dir+"/profile.bin");previous.magic("QFTHP002");uint32_t previousFlags,bytes;previous.read(&previousFlags,4);previous.read(&bytes,4);
    if(bytes<16||bytes>4000000)throw std::runtime_error("Invalid previous profile");previous.file.seekg(bytes, std::ios::beg);
    cv::Mat limits=previous.matrix(4,1);out.add(limits);capabilities|=previousFlags&8;
    out.write(dir+"/profile.pending",capabilities);
}
}
int main(int argc,char **argv) {
    if(argc!=2)return 2;
    try{cv::setNumThreads(1);compile(argv[1]);puts("CALIBRATION_READY");return 0;}
    catch(const std::exception &error){std::fprintf(stderr,"CALIBRATION_FAILED %s\n",error.what());return 1;}
}
