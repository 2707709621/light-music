// QQ web protocol constants and signature derivation adapted from jixunmoe/qmweb-sign (MIT).
// Copyright (c) 2024 Jixun Wu. Full license is embedded in THIRD-PARTY-NOTICES.md.
using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

public sealed partial class QQMusicClient
{
    private async Task<JsonElement> WritePlaylistRequestAsync(AccountSession session,string method,object param,CancellationToken ct)
    {
        var payload=new{
            comm=new{uin=session.Uin,authst=session.MusicKey,tmeLoginType=session.LoginType,ct=24,cv=4747474,platform="yqq.json",format="json",g_tk=Hash33(session.MusicKey,5381),g_tk_new_20200303=Hash33(session.MusicKey,5381),inCharset="utf-8",outCharset="utf-8",notice=0,needNewCode=1},
            req_0=new{module="music.musicasset.PlaylistDetailWrite",method,param}
        };
        byte[] body=JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] digest=SHA1.HashData(body);
        string hex=Convert.ToHexString(digest);
        int[] first={23,14,6,36,16,7,19},last={16,1,32,12,19,27,8,5};
        byte[] scramble={89,39,179,150,218,82,58,252,177,52,186,123,120,64,242,133,143,161,121,179};
        string middle=Convert.ToBase64String(digest.Select((value,index)=>(byte)(value^scramble[index])).ToArray());
        middle=new string(middle.Where(c=>c!='/' && c!='\\' && c!='+' && c!='=').ToArray());
        string sign=("zzc"+new string(first.Select(i=>hex[i]).ToArray())+middle+new string(last.Select(i=>hex[i]).ToArray())).ToLowerInvariant();
        byte[] nonce=RandomNumberGenerator.GetBytes(12),cipher=new byte[body.Length],tag=new byte[16];
        using(var aes=new AesGcm(new byte[]{189,48,95,16,208,255,116,182,239,84,218,184,53,181,225,207},16))aes.Encrypt(nonce,body,cipher,tag);
        byte[] packed=nonce.Concat(cipher).Concat(tag).ToArray();
        string url="https://u6.y.qq.com/cgi-bin/musics.fcg?encoding=ag-1&sign="+sign+"&_="+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var request=Request(HttpMethod.Post,url,SessionCookie(session));
        request.Content=new StringContent(Convert.ToBase64String(packed),Encoding.ASCII,"text/plain");
        using var response=await SendAsync(request,ct).ConfigureAwait(false);
        byte[] bytes=await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        byte[] mask={122,63,140,29,94,155,47,10,108,77,126,139,31,58,92,157,14,43,111,74,129};
        for(int i=0;i<bytes.Length;i++)bytes[i]^=mask[i%mask.Length];
        var root=ParseJson(Encoding.UTF8.GetString(bytes));RequireCode(root,"修改歌单");
        var block=At(root,"req_0");RequireCode(block,"修改歌单");
        var data=At(block,"data");RequireCode(data,"修改歌单");
        return data;
    }
}
