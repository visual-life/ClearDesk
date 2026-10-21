# QRC cache decoding

DESHelper.cs is adapted from [WXRIW/QQMusicDecoder](https://github.com/WXRIW/QQMusicDecoder), revision 0e1494194523dd885405812a91ee9b9702bfb30c. Changes: internal namespace and .NET Framework-compatible array slices. MIT license: LICENSE-WXRIW.

The QMC mask and local QRC container layout used by src/ClientLyrics.cs are adapted from [jixunmoe-go/qrc](https://github.com/jixunmoe-go/qrc), revision 866e996416b0cec7bef648400633f6483c4200d5. MIT license: LICENSE-Jixun.

Only local lyric cache decoding is included; no login, cookie extraction or private cloud API is used.
