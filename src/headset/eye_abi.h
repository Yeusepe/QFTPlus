#ifndef QFT_EYE_ABI_H
#define QFT_EYE_ABI_H
#include <elf.h>
#include "eye_profile.h"
#define EYE_EXECUTE_TYPE "NSt6__ndk110__function6__funcIZN5torch3jit12_GLOBAL__N_114getExecuteFuncIN4bolt2nn13BoltNNBackendEEENS_8functionIFvRNS_6vectorIN3c106IValueENS_9allocatorISC_EEEEEEEvEUlSG_E_NSD_ISJ_EESH_EE"

// Normalize relocations and independently checked layout relationships before comparing instructions.
struct EyeCodeContract { const char *name; size_t length; const char *hash[3]; size_t alternate_length; size_t third_length; };
static const struct EyeCodeContract eye_code_contracts[QFT_EYE_CODE_COUNT] = {
    {"execute", 0, {NULL, NULL, NULL}, 0, 0},
    {"retain", 44, {"8d6fb4aedde7516c736d746187fae008b759608cbad44ebc5d8bab3bbd5fa013", NULL, NULL}, 0, 0},
    {"release", 128, {"bfcf035aa5c355e80f38482544a803403cd328a95a6ccf2d55b7eb7c35557878", NULL, NULL}, 0, 0},
    {"assert", 0, {NULL, NULL, NULL}, 0, 0},
    {"detector", 0, {NULL, NULL, NULL}, 0, 0},
    {"filter_process", 480, {"f58d5a56350e284f7638254231c328fcee90802b328b6611b938a79b5cbe8bbf", NULL, NULL}, 0, 0},
    {"filter_destroy", 132, {"fe4b1cb1d2fcc6efc9e7ee52eba89f6e9ebbf77e32387e1ddb1926d731a47f23", NULL, NULL}, 0, 0},
    {"filter_apply", 1704, {"2bcba004c982b79d4f68b6165cfd4e1ba535d3d4dc04863e5485ec81adabb688", NULL, "0a7b613a0ec6fb561a170624e19c8ecb35b20c21c68eb46fce7c5fcae54b3856"}, 0, 1760},
    {"model_prepare", 0, {NULL, NULL, NULL}, 0, 0},
    {"model_stop", 0, {NULL, NULL, NULL}, 0, 0},
    {"task_dispatch", 0, {NULL, NULL, NULL}, 0, 0},
    {"task_construct", 0, {NULL, NULL, NULL}, 0, 0},
    {"task_run", 0, {NULL, NULL, NULL}, 0, 0},
    {"task_error", 0, {NULL, NULL, NULL}, 0, 0},
};

struct EyeImage { const unsigned char *data; size_t length; Elf64_Phdr segments[256]; unsigned count; uint64_t unwind; size_t unwind_size; };
static struct QftEyeProfile eye_discovered_profile;
static int eye_image_open(struct EyeImage *image,const char *data,size_t length) {
    if(length<sizeof(Elf64_Ehdr))return 0;
    Elf64_Ehdr h;memcpy(&h,data,sizeof h);
    if(memcmp(h.e_ident,ELFMAG,SELFMAG)||h.e_ident[EI_CLASS]!=ELFCLASS64||h.e_ident[EI_DATA]!=ELFDATA2LSB||
       h.e_type!=ET_DYN||h.e_machine!=EM_AARCH64||h.e_phentsize!=sizeof(Elf64_Phdr)||!h.e_phnum||h.e_phnum>256||
       h.e_phoff>length||h.e_phnum>(length-h.e_phoff)/sizeof(Elf64_Phdr))return 0;
    image->data=(const unsigned char*)data;image->length=length;image->count=0;image->unwind=0;image->unwind_size=0;
    for(unsigned i=0;i<h.e_phnum;i++) {
        Elf64_Phdr s;memcpy(&s,data+h.e_phoff+i*sizeof s,sizeof s);
        if(s.p_type==0x6474e550){if(image->unwind)return 0;image->unwind=s.p_vaddr;image->unwind_size=s.p_filesz;}
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
static int eye_detector_layout(uint32_t *words) {
    const unsigned valid=(words[0x18/4]>>10)&4095;
    const unsigned scale=((words[0x3c/4]>>10)&4095)*8;
    if(valid<0x15c||valid>=0x800||(valid&7)!=4||scale<8||scale>=0x800)return 0;
    // Both eye paths must agree; the helper separately proves the axis/validity relationship.
    static const struct {unsigned at,opcode,unit,canonical; int relative;} fields[]={
        {0x18,0x39400288,1,0x35c,0}, {0x8c,0x39400288,1,0x35c,0},
        {0x30,0xbd400280,4,0x2d0,-0x8c}, {0x50,0xbd000280,4,0x2d0,-0x8c},
        {0xa4,0xbd400280,4,0x2d0,-0x8c}, {0xc4,0xbd000280,4,0x2d0,-0x8c},
        {0x60,0xf9400295,8,0x4b8,0x15c}, {0x68,0xf9400296,8,0x4c0,0x164},
    };
    for(unsigned i=0;i<sizeof fields/sizeof *fields;i++) {
        uint32_t word=words[fields[i].at/4];
        if((word&~0x3ffc00u)!=fields[i].opcode||
           ((word>>10)&4095)*fields[i].unit!=(unsigned)((int)valid+fields[i].relative))return 0;
        words[fields[i].at/4]=fields[i].opcode|((fields[i].canonical/fields[i].unit)<<10);
    }
    for(unsigned at=0x3c;at<=0xb0;at+=0x74) {
        uint32_t word=words[at/4];
        if((word&~0x3ffc00u)!=0xfd400101||((word>>10)&4095)*8!=scale)return 0;
        words[at/4]=0xfd400101|(0x60/8<<10);
    }
    return 1;
}
static int eye_filter_stride(uint32_t *words) {
    const unsigned stride=(words[0x38/4]>>10)&4095;
    if(stride<0x570||stride>0x1000||(stride&7))return 0;
    static const unsigned offsets[]={0x38,0x68,0x9c,0xe0,0x10c,0x134};
    for(unsigned i=0;i<sizeof offsets/sizeof *offsets;i++) {
        unsigned at=offsets[i]/4;
        uint32_t word=words[at];
        unsigned reg=offsets[i]==0x9c?1:offsets[i]==0x134?22:9;
        if((word&~0x3ffc00u)!=(0x91000000u|(reg<<5)|reg)||((word>>10)&4095)!=stride)return 0;
        words[at]=(word&~0x3ffc00u)|(0x570<<10);
    }
    return 1;
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
    if(id==QFT_DETECTOR&&!eye_detector_layout(normalized))return 0;
    if(id==QFT_FILTER_PROCESS&&!eye_filter_stride(normalized))return 0;
    gchar *hash=g_compute_checksum_for_data(G_CHECKSUM_SHA256,(const guchar*)normalized,contract->length);
    int valid=!strcmp(hash,contract->hash[0])?1:(contract->hash[1]&&!strcmp(hash,contract->hash[1])?2:0);g_free(hash);
    return valid;
}
static int eye_code_matches(const struct EyeImage *image,uint64_t address,enum QftEyeCode id,uint64_t assertion) {
    const struct EyeCodeContract *contract=&eye_code_contracts[id];
    const size_t lengths[]={contract->length,contract->alternate_length?contract->alternate_length:contract->length,contract->third_length};
    for(unsigned i=0;i<3;i++) {
        if(!contract->hash[i]||!lengths[i])continue;
        struct EyeCodeContract candidate=*contract;
        candidate.length=lengths[i];candidate.hash[0]=contract->hash[i];candidate.hash[1]=NULL;
        if(eye_code_match_length(image,address,id,assertion,&candidate))return i+1;
    }
    return 0;
}
static size_t eye_code_length(const struct EyeImage *image,uint64_t address,enum QftEyeCode id,uint64_t assertion) {
    const struct EyeCodeContract *contract=&eye_code_contracts[id];
    int variant=eye_code_matches(image,address,id,assertion);
    if(variant==3)return contract->third_length;
    return variant?(variant==2&&contract->alternate_length?contract->alternate_length:contract->length):0;
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
static int eye_detector(const struct EyeImage *image,struct QftEyeProfile *profile) {
    uint64_t address=profile->code[QFT_DETECTOR];size_t length=profile->lengths[QFT_DETECTOR]?profile->lengths[QFT_DETECTOR]:eye_code_contracts[QFT_DETECTOR].length;
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
#include "eye_discovery.h"
static int eye_engine_compatible(const char *data,size_t length,int averaging_mode) {
    struct EyeImage image;struct QftEyeProfile profile={0};if(!eye_image_open(&image,data,length))return 0;
    if(!averaging_mode){if(!eye_discover_operations(&image,&profile))return 0;eye_discovered_profile=profile;return 1;}
    if(averaging_mode) {
        profile.filter_vtable=eye_vtable(&image,"N12eye_tracking17InteractionFilterE");if(!profile.filter_vtable)return 0;
        profile.code[QFT_FILTER_DESTROY]=eye_virtual_code(&image,profile.filter_vtable,QFT_FILTER_DESTROY,0);
        profile.code[QFT_FILTER_PROCESS]=eye_virtual_code(&image,profile.filter_vtable,QFT_FILTER_PROCESS,0);
        if(!profile.code[QFT_FILTER_DESTROY]||!profile.code[QFT_FILTER_PROCESS])return 0;
        const unsigned char *calls=eye_image_at(&image,profile.code[QFT_FILTER_PROCESS],480,PF_R|PF_X);if(!calls)return 0;
        uint32_t a=eye_word(calls+0x168),b=eye_word(calls+0x184);
        if((a&0xfc000000)!=0x94000000||(b&0xfc000000)!=0x94000000)return 0;
        profile.code[QFT_FILTER_APPLY]=eye_branch(profile.code[QFT_FILTER_PROCESS]+0x168,a);
        int apply_variant=eye_code_matches(&image,profile.code[QFT_FILTER_APPLY],QFT_FILTER_APPLY,0);
        if(eye_branch(profile.code[QFT_FILTER_PROCESS]+0x184,b)!=profile.code[QFT_FILTER_APPLY]||!apply_variant)return 0;
        // Permit appended record fields only when the validated helper's entire input still fits.
        unsigned stride=(eye_word(calls+0x38)>>10)&4095;
        if(stride<(apply_variant==3?0x5b8u:0x570u))return 0;
    }
    eye_discovered_profile=profile;return 1;
}

#endif
