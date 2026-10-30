/**
 * The participation path, used on the home page and on "Necə iştirak etməli?". A function, because one of the
 * steps names the platform and the platform's name is an admin setting.
 */
export function participationSteps(platformName: string) {
  return [
    {
      title: 'Müsabiqəni seçin',
      text: 'Layihələrə baxın və qəbulu açıq olan müsabiqəni tapın. Hər müsabiqənin öz qaydaları və tələbləri var.',
    },
    {
      title: 'İstiqaməti seçin',
      text: 'Müsabiqədə kateqoriya varsa (məsələn, kənd və ya janr), iştirak edəcəyiniz birini seçin.',
    },
    {
      title: 'Hesaba daxil olun',
      text: `Bir ${platformName} hesabı bütün layihə və müsabiqələr üçün keçərlidir. Seçiminiz yadda qalır.`,
    },
    {
      title: 'İşinizi təqdim edin',
      text: 'Məlumatları doldurun, faylları əlavə edin, göndərməzdən əvvəl baxış keçirin. Sonda istinad nömrəsi alırsınız.',
    },
  ]
}

/**
 * The same path in three steps, for a competition's own page, where the competition has already been chosen and
 * the reader wants to know how much stands between them and an entry. Written as a friend would say it, like the
 * button beside it; each step names what really happens in the form, the sign-in included, so nothing is a surprise.
 */
export const entrySteps = [
  {
    title: 'İdeyanı hazırla',
    text: 'Mövzuya uyğun ideyanı eskiz, maket və ya hazır iş kimi fayla çevir.',
  },
  {
    title: 'Məlumatlarını doldur',
    text: 'Hesabına daxil ol və ya qeydiyyatdan keç, sonra işin adını və izahını yaz.',
  },
  {
    title: 'İşini təqdim et',
    text: 'Faylları yüklə və göndər — sonda istinad nömrəsi alırsan.',
  },
]
