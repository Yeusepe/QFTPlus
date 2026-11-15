#ifndef QFT_EYE_ABI_H
#define QFT_EYE_ABI_H
#include <elf.h>
#include "eye_profile.h"
#define EYE_EXECUTE_TYPE "NSt6__ndk110__function6__funcIZN5torch3jit12_GLOBAL__N_114getExecuteFuncIN4bolt2nn13BoltNNBackendEEENS_8functionIFvRNS_6vectorIN3c106IValueENS_9allocatorISC_EEEEEEEvEUlSG_E_NSD_ISJ_EESH_EE"

// Fingerprints retain instructions and field offsets; only relocated addresses and verified assertion line numbers are normalized.
struct EyeCodeContract { const char *name; size_t length; const char *hash[2]; size_t alternate_length; };
static const struct EyeCodeContract eye_code_contracts[QFT_EYE_CODE_COUNT] = {
    {"execute", 1580, {"71b32b79c4a2e99a471142b1bd92e494607009b5f681e47853953f08895c4127", NULL}, 0},
    {"retain", 44, {"8d6fb4aedde7516c736d746187fae008b759608cbad44ebc5d8bab3bbd5fa013", NULL}, 0},
    {"release", 128, {"bfcf035aa5c355e80f38482544a803403cd328a95a6ccf2d55b7eb7c35557878", NULL}, 0},
    {"assert", 408, {"8bd5d020b114dc31157edc69390b90169dbeedc8dd209ecc78792e5e838b803c", NULL}, 0},
    {"detector", 220, {"68429067ec27a05bf6fb0e10e8ad6a82c9f16e8f1badbb0b7da86ea48eb8feda", "8c8392ac6a65094239c583ee8861f656eb2bf552af4c36816f9f476f0c762edd"}, 0},
    {"filter_process", 480, {"f58d5a56350e284f7638254231c328fcee90802b328b6611b938a79b5cbe8bbf", NULL}, 0},
    {"filter_destroy", 132, {"fe4b1cb1d2fcc6efc9e7ee52eba89f6e9ebbf77e32387e1ddb1926d731a47f23", NULL}, 0},
    {"filter_apply", 1704, {"2bcba004c982b79d4f68b6165cfd4e1ba535d3d4dc04863e5485ec81adabb688", NULL}, 0},
    {"model_prepare", 2768, {"84170fa460e1f5fa58b034ca1a33247bc11917eb266043cea7d8c248dacc8a3b", "2b9c8d585bae191ca6244a595fcf55828f78ff307820d5eda1c19173e6e6907a"}, 2768},
    {"model_stop", 1140, {"04f10e4dd533e51f595a5ff5acde218f4288c73f5eebebadc974aabf2cfbfde5", "d6d060ba15bd13cfc38bb99453a561f9bd5a51d893e731b18046252f3a64b529"}, 1136},
    {"task_dispatch", 396, {"dfac854ef08c052f9e1caa05b7e282575f69c90a91903a32e4f46d0161922911", "5975aa8853c2a5d8b16f010de98e790b6d1a32b6e81474be76ec15bcc98c09d7"}, 404},
    {"task_construct", 1072, {"3af31c304a7c0c0d35ea4c81ffec45c8ae771d52987ed6b0317817ad445c8780", "1df3645fca9f0806b6d4fd98875fce1b85c2741673860cd97094739f2bac6680"}, 1080},
    {"task_run", 620, {"10cafc74c3cf6c1a8a47f37b034031df7ae7a64d95bd9742cb200d6c8889f5ea", "ca97fd15c5413fd18de692897d57c1a0705cf938b13cb0d5d5eec83a3fa41fc3"}, 624},
    {"task_error", 608, {"2c9a616789fe99d0d02bc53f4313dc98e889a003c701dac87f5faa7457f5e346", "f2793836e1d88ecbef9e7428412fd69b35325b0bee3a4408eded5478ea2f154b"}, 620},
};

struct EyeCallContract { unsigned offset, role; };
static const struct EyeCallContract eye_execute_calls[] = {
    {0x94, QFT_RELEASE},
    {0xf0, QFT_RELEASE},
    {0x14c, QFT_RELEASE},
    {0x16c, QFT_RETAIN},
    {0x230, QFT_RETAIN},
    {0x26c, QFT_RETAIN},
    {0x2c8, QFT_RETAIN},
    {0x2ec, QFT_RETAIN},
    {0x338, QFT_RELEASE},
    {0x358, QFT_RETAIN},
    {0x3d0, QFT_RELEASE},
    {0x3f0, QFT_RELEASE},
    {0x400, QFT_RELEASE},
    {0x450, QFT_ASSERT},
    {0x470, QFT_ASSERT},
    {0x490, QFT_ASSERT},
    {0x4b0, QFT_ASSERT},
    {0x4d0, QFT_ASSERT},
    {0x4f0, QFT_ASSERT},
    {0x510, QFT_ASSERT},
    {0x530, QFT_ASSERT},
};

struct EyeImage { const unsigned char *data; size_t length; Elf64_Phdr segments[256]; unsigned count; };
static struct QftEyeProfile eye_discovered_profile;
static int eye_image_open(struct EyeImage *image,const char *data,size_t length) {
    if(length<sizeof(Elf64_Ehdr))return 0;
    Elf64_Ehdr h;memcpy(&h,data,sizeof h);
    if(memcmp(h.e_ident,ELFMAG,SELFMAG)||h.e_ident[EI_CLASS]!=ELFCLASS64||h.e_ident[EI_DATA]!=ELFDATA2LSB||
       h.e_type!=ET_DYN||h.e_machine!=EM_AARCH64||h.e_phentsize!=sizeof(Elf64_Phdr)||!h.e_phnum||h.e_phnum>256||
       h.e_phoff>length||h.e_phnum>(length-h.e_phoff)/sizeof(Elf64_Phdr))return 0;
    image->data=(const unsigned char*)data;image->length=length;image->count=0;
    for(unsigned i=0;i<h.e_phnum;i++) {
        Elf64_Phdr s;memcpy(&s,data+h.e_phoff+i*sizeof s,sizeof s);
        if(s.p_type!=PT_LOAD)continue;
        if(s.p_filesz>s.p_memsz||s.p_offset>length||s.p_filesz>length-s.p_offset||s.p_vaddr>UINT64_MAX-s.p_memsz)return 0;
        image->segments[image->count++]=s;
    }
    return image->count!=0;
}
static const unsigned char *eye_image_at(const struct EyeImage *image,uint64_t address,size_t length,unsigned flags) {
    const unsigned char *result=NULL;unsigned count=0;
    for(unsigned i=0;i<image->count;i++) {
        const Elf64_Phdr *s=&image->segments[i];
        if(address<s->p_vaddr)continue;
        uint64_t offset=address-s->p_vaddr;
        if(offset>s->p_filesz||length>s->p_filesz-offset)continue;
        if((s->p_flags&flags)!=flags)return NULL;
        result=image->data+s->p_offset+offset;count++;
    }
    return count==1?result:NULL;
}
static uint32_t eye_word(const unsigned char *p) {uint32_t value;memcpy(&value,p,4);return value;}
static uint64_t eye_pointer(const struct EyeImage *image,uint64_t address) {
    const unsigned char *p=eye_image_at(image,address,8,PF_R);uint64_t value=0;
    if(p)memcpy(&value,p,8);
    return value;
}
static uint64_t eye_find(const struct EyeImage *image,const void *pattern,size_t length,unsigned flags,int aligned) {
    uint64_t result=0;unsigned matches=0;
    for(unsigned i=0;i<image->count;i++) {
        const Elf64_Phdr *s=&image->segments[i];if((s->p_flags&flags)!=flags)continue;
        const unsigned char *start=image->data+s->p_offset,*cursor=start,*end=start+s->p_filesz;
        while(length<=(size_t)(end-cursor)) {
            const unsigned char *found=memmem(cursor,(size_t)(end-cursor),pattern,length);if(!found)break;
            uint64_t address=s->p_vaddr+(uint64_t)(found-start);
            if(!aligned||!(address&7)){result=address;matches++;}
            cursor=found+1;
        }
    }
    return matches==1?result:0;
}
static uint64_t eye_vtable(const struct EyeImage *image,const char *name) {
    uint64_t label=eye_find(image,name,strlen(name)+1,PF_R,0);if(!label)return 0;
    uint64_t reference=eye_find(image,&label,8,PF_R|PF_W,1);if(reference<8)return 0;
    uint64_t typeinfo=reference-8;
    uint64_t result=0;
    for(unsigned i=0;i<image->count;i++) {
        const Elf64_Phdr *s=&image->segments[i];if((s->p_flags&(PF_R|PF_W))!=(PF_R|PF_W))continue;
        for(uint64_t offset=8;offset+72<=s->p_filesz;offset+=8) {
            reference=s->p_vaddr+offset;
            if(eye_pointer(image,reference)!=typeinfo||eye_pointer(image,reference-8)!=0||
               !eye_image_at(image,eye_pointer(image,reference+8),4,PF_R|PF_X))continue;
            if(result)return 0;
            result=reference+8;
        }
    }
    return result;
}
static uint64_t eye_branch(uint64_t address,uint32_t word) {
    int64_t displacement=(int64_t)(word&0x3ffffff);
    if(displacement&0x2000000)displacement-=0x4000000;
    return address+(uint64_t)(displacement*4);
}
static int eye_code_match_length(const struct EyeImage *image,uint64_t address,enum QftEyeCode id,uint64_t assertion,const struct EyeCodeContract *contract) {
    const unsigned char *code=eye_image_at(image,address,contract->length,PF_R|PF_X);if(!address||!code||(address&3))return 0;
    uint32_t normalized[1600];unsigned char pages[32]={0};
    if(contract->length>sizeof normalized||contract->length%4)return 0;
    if(id==QFT_FILTER_PROCESS) {
        uint32_t a=eye_word(code+16),b=eye_word(code+344),c=eye_word(code+372);
        unsigned input=((a>>15)&127)*8,history=(b>>10)&4095;
        if((a&~(127u<<15))!=0xa9402036u||(b&~0x3ffc00u)!=0x910002a4u||
           (c&~0x3ffc00u)!=(b&~0x3ffc00u)||((c>>10)&4095)!=history||
           input>0x800||history-input!=0x1d8)return 0;
    }
    for(size_t i=0;i<contract->length/4;i++) {
        uint32_t word=eye_word(code+i*4),value=word;unsigned rd=word&31,rn=(word>>5)&31;
        if((word&0xffe0001f)==0x52800002&&i*4+8<=contract->length) {
            uint32_t next=eye_word(code+i*4+4);
            if((next&0xfc000000)==0x94000000&&eye_branch(address+i*4+4,next)==assertion)value&=~0x1fffe0u;
        }
        if((word&0x9f000000)==0x90000000){value=word&0x9f00001f;pages[rd]=1;}
        else if((word&0x9f000000)==0x10000000){value=word&0x9f00001f;pages[rd]=0;}
        else if((word&0x7c000000)==0x14000000) {
            uint64_t target=eye_branch(address+i*4,word);
            if((word&0x80000000)||target<address||target-address>=contract->length)value=word&0xfc000000;
            if(word&0x80000000){memset(pages,0,19);pages[30]=0;}
        } else if((word&0x7f000000)==0x11000000) {
            if(pages[rn])value&=~0x3ffc00u;
            pages[rd]=0;
        } else if((word&0x3b000000)==0x39000000) {
            if(pages[rn])value&=~0x3ffc00u;
            if(word&(1u<<22))pages[rd]=0;
        } else if((word&0x3a000000)==0x28000000) {
            if(word&(1u<<22)){pages[rd]=0;pages[(word>>10)&31]=0;}
            if(((word>>23)&3)!=2)pages[rn]=0;
        } else pages[rd]=0;
        if(id==QFT_FILTER_PROCESS&&(i*4==16||i*4==344||i*4==372))
            value=word&~(i*4==16?(127u<<15):0x3ffc00u);
        normalized[i]=value;
    }
    gchar *hash=g_compute_checksum_for_data(G_CHECKSUM_SHA256,(const guchar*)normalized,contract->length);
    int valid=!strcmp(hash,contract->hash[0])?1:(contract->hash[1]&&!strcmp(hash,contract->hash[1])?2:0);g_free(hash);
    return valid;
}
static int eye_code_matches(const struct EyeImage *image,uint64_t address,enum QftEyeCode id,uint64_t assertion) {
    const struct EyeCodeContract *contract=&eye_code_contracts[id];
    int result=eye_code_match_length(image,address,id,assertion,contract);
    if(result||!contract->alternate_length||contract->alternate_length==contract->length)return result;
    struct EyeCodeContract alternate=*contract;alternate.length=contract->alternate_length;
    alternate.hash[0]=contract->hash[1];alternate.hash[1]=NULL;
    return eye_code_match_length(image,address,id,assertion,&alternate)?2:0;
}
static size_t eye_code_length(const struct EyeImage *image,uint64_t address,enum QftEyeCode id,uint64_t assertion) {
    const struct EyeCodeContract *contract=&eye_code_contracts[id];
    int variant=eye_code_matches(image,address,id,assertion);
    return variant?(variant==2&&contract->alternate_length?contract->alternate_length:contract->length):0;
}
static uint64_t eye_called_code(const struct EyeImage *image,uint64_t caller,enum QftEyeCode caller_id,enum QftEyeCode wanted,uint64_t assertion) {
    size_t length=eye_code_length(image,caller,caller_id,assertion);
    const unsigned char *code=eye_image_at(image,caller,length,PF_R|PF_X);if(!code)return 0;
    uint64_t result=0;
    for(size_t i=0;i<length;i+=4) {
        uint32_t word=eye_word(code+i);if((word&0xfc000000)!=0x94000000)continue;
        uint64_t target=eye_branch(caller+i,word);
        if(target==result||!eye_code_matches(image,target,wanted,assertion))continue;
        if(result)return 0;
        result=target;
    }
    return result;
}
static uint64_t eye_virtual_code(const struct EyeImage *image,uint64_t table,enum QftEyeCode wanted,uint64_t assertion) {
    uint64_t result=0;
    for(unsigned i=0;i<16;i++) {
        uint64_t target=eye_pointer(image,table+i*8);
        if(!target||!eye_image_at(image,target,4,PF_R|PF_X))break;
        if(target==result||!eye_code_matches(image,target,wanted,assertion))continue;
        if(result)return 0;
        result=target;
    }
    return result;
}
static int eye_execute_code(const struct EyeImage *image,uint64_t table,struct QftEyeProfile *profile) {
    for(unsigned i=0;i<16;i++) {
        uint64_t target=eye_pointer(image,table+i*8);
        if(!target||!eye_image_at(image,target,4,PF_R|PF_X))break;
        const unsigned char *call=eye_image_at(image,target+0x4b0,4,PF_R|PF_X);
        if(!call||(eye_word(call)&0xfc000000)!=0x94000000)continue;
        uint64_t assertion=eye_branch(target+0x4b0,eye_word(call));
        if(!eye_code_matches(image,assertion,QFT_ASSERT,assertion)||!eye_code_matches(image,target,QFT_EXECUTE,assertion))continue;
        if(profile->code[QFT_EXECUTE]&&profile->code[QFT_EXECUTE]!=target)return 0;
        profile->code[QFT_EXECUTE]=target;profile->code[QFT_ASSERT]=assertion;
    }
    return profile->code[QFT_EXECUTE]!=0;
}
static int eye_call_relationships(const struct EyeImage *image,const struct QftEyeProfile *profile) {
    uint64_t address=profile->code[QFT_EXECUTE];
    const unsigned char *code=eye_image_at(image,address,eye_code_contracts[QFT_EXECUTE].length,PF_R|PF_X);if(!code)return 0;
    for(size_t i=0;i<sizeof eye_execute_calls/sizeof *eye_execute_calls;i++) {
        const struct EyeCallContract *call=&eye_execute_calls[i];uint32_t word=eye_word(code+call->offset);
        if((word&0xfc000000)!=0x94000000||eye_branch(address+call->offset,word)!=profile->code[call->role])return 0;
    }
    return 1;
}
static int eye_task_relationships(const struct EyeImage *image,const struct QftEyeProfile *profile) {
    static const struct {unsigned caller,target,offset[2];} calls[]={
        {QFT_MODEL_PREPARE,QFT_TASK_DISPATCH,{0x21c,0x21c}},
        {QFT_MODEL_PREPARE,QFT_TASK_DISPATCH,{0x2a4,0x2a4}},
        {QFT_MODEL_PREPARE,QFT_TASK_DISPATCH,{0x3c4,0x3c4}},
        {QFT_MODEL_STOP,QFT_TASK_DISPATCH,{0xd4,0xd0}},
        {QFT_MODEL_STOP,QFT_TASK_DISPATCH,{0x210,0x20c}},
        {QFT_TASK_DISPATCH,QFT_TASK_CONSTRUCT,{0xcc,0xd4}},
        {QFT_TASK_DISPATCH,QFT_TASK_RUN,{0xd8,0xe0}},
        {QFT_TASK_RUN,QFT_TASK_ERROR,{0x164,0x168}},
    };
    for(unsigned i=0;i<sizeof calls/sizeof *calls;i++) {
        uint64_t caller=profile->code[calls[i].caller];
        int variant=eye_code_matches(image,caller,(enum QftEyeCode)calls[i].caller,profile->code[QFT_ASSERT]);
        if(variant<1||variant>2)return 0;
        uint64_t address=caller+calls[i].offset[variant-1];
        const unsigned char* p=eye_image_at(image,address,4,PF_R|PF_X);if(!p)return 0;
        uint32_t word=eye_word(p);
        if((word&0xfc000000)!=0x94000000||eye_branch(address,word)!=profile->code[calls[i].target])return 0;
    }
    return 1;
}
static int eye_detector(const struct EyeImage *image,struct QftEyeProfile *profile) {
    uint64_t address=profile->code[QFT_DETECTOR];size_t length=eye_code_contracts[QFT_DETECTOR].length;
    const unsigned char *code=eye_image_at(image,address,length,PF_R|PF_X);if(!code)return 0;
    uint32_t valid=eye_word(code+24);unsigned input=(valid>>10)&4095;
    if((valid&0xffc003ff)!=0x39400288)return 0;
    uint64_t helper=0;unsigned calls=0;
    for(size_t i=8;i<length;i+=4) {
        uint32_t word=eye_word(code+i);
        if((word&0xfc000000)!=0x94000000||eye_word(code+i-8)!=0xaa1303e0||eye_word(code+i-4)!=0xaa1403e1)continue;
        uint64_t target=eye_branch(address+i,word);if(calls==2||(helper&&target!=helper))return 0;
        helper=target;profile->trace[calls++]=address+i+4;
    }
    if(calls!=2)return 0;
    uint32_t words[1024];size_t count=0;int retained=0,input_found=0;
    while(count<1024) {
        const unsigned char *p=eye_image_at(image,helper+count*4,4,PF_R|PF_X);if(!p)return 0;
        uint32_t word=eye_word(p);words[count++]=word;
        if(count<=32){retained|=word==0xaa0103f3;input_found|=word==(0x39400028|(input<<10));}
        if(word==0xd65f03c0)break;
    }
    if(count==1024||!retained||!input_found)return 0;
    unsigned matches=0;
    for(size_t i=0;i+5<count;i++) {
        if((words[i]&0xffc003ff)!=0xfd000260||(words[i+1]&0xffc003ff)!=0xbd000261)continue;
        unsigned axis=((words[i]>>10)&4095)*8,z=((words[i+1]>>10)&4095)*4;
        if(axis<0x100||axis>=0x1000||z!=axis+8||input!=axis+0x5c||words[i+2]!=0xf9401688||words[i+3]!=0xf85d83a9||words[i+4]!=0xeb09011f)continue;
        int x=0,y=0,v=0;
        for(size_t j=0;j<i;j++){x|=words[j]==(0xfd400260|(((axis+0x50)/8)<<10));y|=words[j]==(0xbd400261|(((axis+0x58)/4)<<10));v|=words[j]==(0x39000268|((axis+12)<<10));}
        if(x&&y&&v){profile->axis=axis;matches++;}
    }
    if(matches!=1)return 0;
    for(unsigned i=0;i<2;i++) {
        const unsigned char *p=eye_image_at(image,profile->trace[i],4,PF_R|PF_X);if(!p)return 0;
        profile->trace[i]=(uint64_t)(p-image->data);
    }
    return 1;
}
static int eye_engine_compatible(const char *data,size_t length,int averaging_mode) {
    struct EyeImage image;struct QftEyeProfile profile={0};if(!eye_image_open(&image,data,length))return 0;
    if(averaging_mode) {
        profile.filter_vtable=eye_vtable(&image,"N12eye_tracking17InteractionFilterE");if(!profile.filter_vtable)return 0;
        profile.code[QFT_FILTER_DESTROY]=eye_virtual_code(&image,profile.filter_vtable,QFT_FILTER_DESTROY,0);
        profile.code[QFT_FILTER_PROCESS]=eye_virtual_code(&image,profile.filter_vtable,QFT_FILTER_PROCESS,0);
        if(!profile.code[QFT_FILTER_DESTROY]||!profile.code[QFT_FILTER_PROCESS])return 0;
        const unsigned char *calls=eye_image_at(&image,profile.code[QFT_FILTER_PROCESS],480,PF_R|PF_X);if(!calls)return 0;
        uint32_t a=eye_word(calls+0x168),b=eye_word(calls+0x184);
        if((a&0xfc000000)!=0x94000000||(b&0xfc000000)!=0x94000000)return 0;
        profile.code[QFT_FILTER_APPLY]=eye_branch(profile.code[QFT_FILTER_PROCESS]+0x168,a);
        if(eye_branch(profile.code[QFT_FILTER_PROCESS]+0x184,b)!=profile.code[QFT_FILTER_APPLY]||
           !eye_code_matches(&image,profile.code[QFT_FILTER_APPLY],QFT_FILTER_APPLY,0))return 0;
    } else {
        profile.backend_vtable=eye_vtable(&image,"N4bolt2nn13BoltNNBackendE");
        uint64_t execute=eye_vtable(&image,EYE_EXECUTE_TYPE);
        if(!profile.backend_vtable||!execute)return 0;
        if(!eye_execute_code(&image,execute,&profile))return 0;
        const unsigned char *call=eye_image_at(&image,profile.code[QFT_EXECUTE]+0x94,4,PF_R|PF_X);
        if(!call||(eye_word(call)&0xfc000000)!=0x94000000)return 0;
        profile.code[QFT_RELEASE]=eye_branch(profile.code[QFT_EXECUTE]+0x94,eye_word(call));
        if(!eye_code_matches(&image,profile.code[QFT_RELEASE],QFT_RELEASE,profile.code[QFT_ASSERT]))return 0;
        profile.code[QFT_RETAIN]=eye_called_code(&image,profile.code[QFT_EXECUTE],QFT_EXECUTE,QFT_RETAIN,profile.code[QFT_ASSERT]);
        if(!profile.code[QFT_RETAIN]||!eye_call_relationships(&image,&profile))return 0;
        uint64_t detector=eye_vtable(&image,"N12eye_tracking18VisualAxisDetectorE");if(!detector)return 0;
        profile.code[QFT_DETECTOR]=eye_virtual_code(&image,detector,QFT_DETECTOR,profile.code[QFT_ASSERT]);
        if(!profile.code[QFT_DETECTOR]||!eye_detector(&image,&profile))return 0;
        profile.model_vtable=eye_vtable(&image,"N4bolt2nn14HexagonNNModelE");if(!profile.model_vtable)return 0;
        profile.code[QFT_MODEL_PREPARE]=eye_virtual_code(&image,profile.model_vtable,QFT_MODEL_PREPARE,profile.code[QFT_ASSERT]);
        profile.code[QFT_MODEL_STOP]=eye_virtual_code(&image,profile.model_vtable,QFT_MODEL_STOP,profile.code[QFT_ASSERT]);
        if(!profile.code[QFT_MODEL_PREPARE]||!profile.code[QFT_MODEL_STOP])return 0;
        profile.code[QFT_TASK_DISPATCH]=eye_called_code(&image,profile.code[QFT_MODEL_PREPARE],QFT_MODEL_PREPARE,QFT_TASK_DISPATCH,profile.code[QFT_ASSERT]);
        if(!profile.code[QFT_TASK_DISPATCH]||eye_called_code(&image,profile.code[QFT_MODEL_STOP],QFT_MODEL_STOP,QFT_TASK_DISPATCH,profile.code[QFT_ASSERT])!=profile.code[QFT_TASK_DISPATCH])return 0;
        profile.code[QFT_TASK_CONSTRUCT]=eye_called_code(&image,profile.code[QFT_TASK_DISPATCH],QFT_TASK_DISPATCH,QFT_TASK_CONSTRUCT,profile.code[QFT_ASSERT]);
        profile.code[QFT_TASK_RUN]=eye_called_code(&image,profile.code[QFT_TASK_DISPATCH],QFT_TASK_DISPATCH,QFT_TASK_RUN,profile.code[QFT_ASSERT]);
        if(!profile.code[QFT_TASK_CONSTRUCT]||!profile.code[QFT_TASK_RUN])return 0;
        profile.code[QFT_TASK_ERROR]=eye_called_code(&image,profile.code[QFT_TASK_RUN],QFT_TASK_RUN,QFT_TASK_ERROR,profile.code[QFT_ASSERT]);
        if(!profile.code[QFT_TASK_ERROR]||!eye_task_relationships(&image,&profile))return 0;
    }
    eye_discovered_profile=profile;return 1;
}

#endif
