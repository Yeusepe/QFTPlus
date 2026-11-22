#ifndef QFT_EYE_DISCOVERY_H
#define QFT_EYE_DISCOVERY_H

// Use the ELF unwind index for boundaries; compiler-generated function sizes are not an ABI.
static size_t eye_function_length(const struct EyeImage *im,uint64_t address) {
    if(!im->unwind||im->unwind_size<12)return 0;
    const unsigned char *h=eye_image_at(im,im->unwind,im->unwind_size,PF_R);
    if(!h||h[0]!=1||h[1]!=0x1b||h[2]!=3||h[3]!=0x3b)return 0;
    unsigned count=eye_word(h+8);if(count>(im->unwind_size-12)/8)return 0;
    unsigned lo=0,hi=count;
    while(lo<hi){unsigned mid=lo+(hi-lo)/2;uint64_t pc=im->unwind+(int32_t)eye_word(h+12+mid*8);if(pc<address)lo=mid+1;else hi=mid;}
    if(lo==count||im->unwind+(int32_t)eye_word(h+12+lo*8)!=address)return 0;
    uint64_t fde=im->unwind+(int32_t)eye_word(h+16+lo*8);
    const unsigned char *f=eye_image_at(im,fde,16,PF_R);if(!f)return 0;
    unsigned size=eye_word(f);if(size<12||size>4096||!eye_image_at(im,fde,size+4,PF_R))return 0;
    uint64_t cie=fde+4-eye_word(f+4);
    const unsigned char *c=eye_image_at(im,cie,16,PF_R);
    // These libraries encode FDE starts as PC-relative signed words and ranges as words.
    if(!c||eye_word(c+4)!=0||c[8]!=1)return 0;
    unsigned end=eye_word(c)+4;if(end<16||end>256)return 0;
    c=eye_image_at(im,cie,end,PF_R);if(!c)return 0;
    unsigned p=9;while(p<end&&c[p])p++;if(p==end||c[9]!='z')return 0;
    unsigned aug_end=p++;for(unsigned n=0;n<3;n++){unsigned bytes=0;do{if(p>=end||++bytes>10)return 0;}while(c[p++]&128);}
    if(p>=end||c[p]&128)return 0;
    unsigned aug_size=c[p++];if(aug_size>end-p)return 0;
    unsigned limit=p+aug_size,encoding=0;
    for(unsigned a=10;a<aug_end;a++) {
        if(p>=limit)return 0;
        if(c[a]=='R')encoding=c[p++];
        else if(c[a]=='L')p++;
        else if(c[a]=='P'){unsigned e=c[p++]&0x7f;unsigned bytes=e==0x1b?4:e==0x1c?8:0;if(!bytes||limit-p<bytes)return 0;p+=bytes;}
        else return 0;
    }
    if(encoding!=0x1b||p!=limit||fde+8+(int32_t)eye_word(f+8)!=address)return 0;
    size_t length=eye_word(f+12);
    return length&&length<=65536&&!(length&3)&&eye_image_at(im,address,length,PF_R|PF_X)?length:0;
}
static int eye_has_word(const struct EyeImage *im,uint64_t fn,uint32_t mask,uint32_t value) {
    size_t n=eye_function_length(im,fn);const unsigned char *p=eye_image_at(im,fn,n,PF_R|PF_X);if(!n||!p)return 0;
    for(size_t i=0;i<n;i+=4)if((eye_word(p+i)&mask)==value)return 1;
    return 0;
}
static unsigned eye_argument_aliases(const struct EyeImage *im,uint64_t fn,unsigned arg) {
    size_t n=eye_function_length(im,fn);const unsigned char *p=eye_image_at(im,fn,n,PF_R|PF_X);unsigned aliases=1u<<arg;
    if(!n||!p)return 0;
    for(size_t i=0;i<n;i+=4) {
        uint32_t w=eye_word(p+i);
        if((w&0xfc000000)==0x94000000)break;
        if((w&0xffe0ffe0)==0xaa0003e0&&aliases&(1u<<((w>>16)&31)))aliases|=1u<<(w&31);
    }
    return aliases;
}
static int eye_field(const struct EyeImage *im,uint64_t fn,unsigned aliases,uint32_t opcode,unsigned offset,unsigned unit,int source) {
    size_t n=eye_function_length(im,fn);const unsigned char *p=eye_image_at(im,fn,n,PF_R|PF_X);if(!n||!p)return 0;
    for(size_t i=0;i<n;i+=4){uint32_t w=eye_word(p+i);if((w&0xffc00000)==opcode&&aliases&(1u<<((w>>5)&31))&&((w>>10)&4095)*unit==offset&&(source<0||(w&31)==(unsigned)source))return 1;}
    return 0;
}
static unsigned eye_function_calls(const struct EyeImage *im,uint64_t fn,uint64_t *calls,unsigned maximum) {
    size_t n=eye_function_length(im,fn);const unsigned char *p=eye_image_at(im,fn,n,PF_R|PF_X);unsigned count=0;if(!n||!p)return 0;
    for(size_t i=0;i<n;i+=4){uint32_t w=eye_word(p+i);if((w&0xfc000000)!=0x94000000)continue;uint64_t target=eye_branch(fn+i,w);unsigned j=0;for(;j<count;j++)if(calls[j]==target)break;if(j<count)continue;if(count==maximum)return 0;calls[count++]=target;}
    return count;
}
static unsigned eye_call_count(const struct EyeImage *im,uint64_t fn,uint64_t target) {
    size_t n=eye_function_length(im,fn);const unsigned char *p=eye_image_at(im,fn,n,PF_R|PF_X);unsigned count=0;if(!n||!p)return 0;
    for(size_t i=0;i<n;i+=4){uint32_t w=eye_word(p+i);if((w&0xfc000000)==0x94000000&&eye_branch(fn+i,w)==target)count++;}return count;
}
static int eye_model_operation(const struct EyeImage *im,uint64_t fn,int prepare) {
    unsigned self=eye_argument_aliases(im,fn,0);if(!self)return 0;
    if(!eye_field(im,fn,self,0x39400000,0x1f0,1,-1))return 0;
    if(!eye_has_word(im,fn,0xffffffe0,0x52800000|((prepare?2006:2007)<<5)))return 0;
    if(prepare)return eye_field(im,fn,self,0xb9000000,0x210,4,1);
    return eye_field(im,fn,self,0x39000000,0x1f0,1,31)&&eye_field(im,fn,self,0xb9400000,0x1c0,4,-1);
}
static int eye_dispatch_operation(const struct EyeImage *im,uint64_t fn) {
    unsigned options=eye_argument_aliases(im,fn,6);
    return eye_field(im,fn,options,0xf9400000,0,8,-1)&&eye_field(im,fn,options,0xf9400000,8,8,-1)&&
        eye_field(im,fn,options,0x39400000,16,1,-1)&&eye_field(im,fn,options,0x39400000,17,1,-1)&&eye_field(im,fn,options,0x39400000,18,1,-1);
}
struct EyeCallGraph {uint64_t functions[512];unsigned depths[512],count;};
static int eye_walk_calls(const struct EyeImage *im,struct EyeCallGraph *graph,uint64_t fn,unsigned depth) {
    unsigned found=graph->count;for(unsigned i=0;i<graph->count;i++)if(graph->functions[i]==fn){found=i;break;}
    if(found<graph->count&&graph->depths[found]>=depth)return 1;
    if(found==graph->count){if(graph->count==512)return 0;graph->functions[graph->count++]=fn;}
    graph->depths[found]=depth;if(!depth)return 1;
    uint64_t calls[128];unsigned n=eye_function_calls(im,fn,calls,128);
    for(unsigned i=0;i<n;i++)if(eye_function_length(im,calls[i])&&!eye_walk_calls(im,graph,calls[i],depth-1))return 0;
    return 1;
}
static int eye_discover_operations(const struct EyeImage *im,struct QftEyeProfile *p) {
    p->backend_vtable=eye_vtable(im,"N4bolt2nn13BoltNNBackendE");
    p->model_vtable=eye_vtable(im,"N4bolt2nn14HexagonNNModelE");
    uint64_t execute=eye_vtable(im,EYE_EXECUTE_TYPE);if(!execute||!p->backend_vtable||!p->model_vtable)return 0;
    for(unsigned i=0;i<16;i++) {
        uint64_t fn=eye_pointer(im,p->model_vtable+i*8);if(!eye_function_length(im,fn))break;
        for(unsigned prep=0;prep<2;prep++)if(eye_model_operation(im,fn,prep)){
            unsigned role=prep?QFT_MODEL_PREPARE:QFT_MODEL_STOP;if(p->code[role]&&p->code[role]!=fn)return 0;p->code[role]=fn;
        }
    }
    if(!p->code[QFT_MODEL_PREPARE]||!p->code[QFT_MODEL_STOP])return 0;
    for(unsigned i=0;i<16;i++) {
        uint64_t fn=eye_pointer(im,execute+i*8);if(!eye_function_length(im,fn))break;
        uint64_t calls[128],retain=0,release=0;unsigned n=eye_function_calls(im,fn,calls,128);
        for(unsigned c=0;c<n;c++) {
            if(eye_code_matches(im,calls[c],QFT_RETAIN,0)){if(retain&&retain!=calls[c])return 0;retain=calls[c];}
            if(eye_code_matches(im,calls[c],QFT_RELEASE,0)){if(release&&release!=calls[c])return 0;release=calls[c];}
        }
        if(!retain||!release||eye_call_count(im,fn,retain)<3||eye_call_count(im,fn,release)<3)continue;
        if(!eye_has_word(im,fn,0xffc003e0,0xa9400020))continue;
        if(p->code[QFT_EXECUTE]&&p->code[QFT_EXECUTE]!=fn)return 0;
        p->code[QFT_EXECUTE]=fn;p->code[QFT_RETAIN]=retain;p->code[QFT_RELEASE]=release;
    }
    if(!p->code[QFT_EXECUTE])return 0;
    struct EyeCallGraph prepare={0},stop={0};
    if(!eye_walk_calls(im,&prepare,p->code[QFT_MODEL_PREPARE],3)||!eye_walk_calls(im,&stop,p->code[QFT_MODEL_STOP],2))return 0;
    for(unsigned i=0;i<prepare.count;i++) {
        uint64_t fn=prepare.functions[i];if(!eye_dispatch_operation(im,fn))continue;
        int shared=0;for(unsigned j=0;j<stop.count;j++)shared|=stop.functions[j]==fn;if(!shared)continue;
        if(p->code[QFT_TASK_DISPATCH]&&p->code[QFT_TASK_DISPATCH]!=fn)return 0;p->code[QFT_TASK_DISPATCH]=fn;
    }
    if(!p->code[QFT_TASK_DISPATCH])return 0;
    p->task_options_size=eye_field(im,p->code[QFT_TASK_DISPATCH],eye_argument_aliases(im,p->code[QFT_TASK_DISPATCH],6),0xf9400000,24,8,-1)?32:24;
    uint64_t detector=eye_vtable(im,"N12eye_tracking18VisualAxisDetectorE");if(!detector)return 0;
    for(unsigned i=0;i<16;i++) {
        uint64_t fn=eye_pointer(im,detector+i*8);size_t n=eye_function_length(im,fn);if(!n)break;
        const unsigned char *body=eye_image_at(im,fn,n,PF_R|PF_X);
        if(n<36||!body||(eye_word(body+16)&0xffc003ff)!=0xf9400034||eye_word(body+20)!=0xaa0003f3)continue;
        struct QftEyeProfile candidate=*p;candidate.code[QFT_DETECTOR]=fn;candidate.lengths[QFT_DETECTOR]=n;
        if(!eye_detector(im,&candidate))continue;
        if(p->code[QFT_DETECTOR]&&p->code[QFT_DETECTOR]!=fn)return 0;*p=candidate;
    }
    if(!p->code[QFT_DETECTOR])return 0;
    for(unsigned i=0;i<QFT_EYE_CODE_COUNT;i++)if(p->code[i]){p->lengths[i]=eye_function_length(im,p->code[i]);if(!p->lengths[i])return 0;}
    return 1;
}
#endif
