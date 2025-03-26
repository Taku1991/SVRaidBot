# The Chronicles of Team Rocket: Pokémon Raid Edition

*In which we discover that some people have WAY too much time on their hands...*

Ah, Team Rocket. The lovable bumblers from Pokémon whose motto might as well be "failing upward since 1996." But who knew their legacy would live on in the real world? Turns out, digital theft is the new black, and I've had front-row seats to this circus. Buckle up for my riveting investigation into the wild world of Raid Bot thieves!

## 🚀 Team Rocket: Jessie and James Edition

First up in our parade of digital desperados: Kai (aka "BakaKaito," which roughly translates to "Idiot Kai" - at least he's self-aware) and his trusty sidekick Reedy. I mentally cast Kai as Jessie and Reedy as James because, well, the incompetence levels match perfectly.

These two masterminds decided to scrape the HTML of my website to steal raid codes. Revolutionary! Groundbreaking! Absolutely... basic. They created a program that would grab the raid code directly from my source code. 

![Source Code Exposure](https://genpkm.com/images/source_code.jpg)

This brilliant heist was foiled by... me changing where I put the code. *Slow clap*. Thanks to their sloppiness, I now have their IP addresses and location info. Nothing like leaving digital fingerprints all over the crime scene!

Reedy was even kind enough to send me an email with a screenshot of their program. You know, just in case I needed evidence:

![Kai's Sophisticated Program](https://genpkm.com/images/kai_program.webp)

And here's an email claiming Kai has the brains to get them working again:

![Reedy's Email](https://genpkm.com/images/reedy_email.jpg)

Just like in the TV show, this Team Rocket gave me multiple laughs. "Blasting off agaaaaaain!" ✨

## 🇫🇷 Team Rocket: The French Connection

Our next contestants hail from the fictional French region of Pokémon. Very mysterious, very baguette, very... basic in their approach.

I only know them as "X_Latique" and "ririshadow#0" (ID: 263923218430164993). These croissant-munching digital pirates were using a subscriber to my webhook with a Man-In-The-Middle bot to pass raids to other servers. 

This sophisticated technique (that a coding bootcamp graduate could devise in their sleep) was quickly thwarted. I had some fun crashing their servers with some special embeds:

![French Servers Getting Served](https://genpkm.com/images/french_servers.jpg)

*Omelette du fromage*, indeed.

## 👔 Team Rocket Executive Edition: Arlo & Cliff

Now these guys... these guys gave me a run for my money. Kevdog and Moot (Kevdog's programmer, or as I like to call him, "the brain cell of the operation") were the most persistent of the bunch.

Turns out, Kevdog was stealing not just my raids, but ANY raid embed he could get his greedy little mitts on for his server of 91,000 members. Quite the enterprise!

![Kevdog Begins His Crime Spree](https://genpkm.com/images/kevdog_begins.jpg)

Compare my legitimate raid to their knockoff version:

![Legit Raid](https://genpkm.com/images/legit_raid.jpg) | ![Stolen Raid](https://genpkm.com/images/stolen_raid.jpg)

What followed was a digital cat-and-mouse game that would make Tom and Jerry proud:

1. I converted raid codes to images
2. They used OCR to extract the codes
   ![Kevdog Round 2](https://genpkm.com/images/kevdog_2.jpg)
3. I added captcha-like text
4. They posted my entire image instead
   ![Kevdog Round 3](https://genpkm.com/images/kevdog_3.jpg)
5. I added my website to the image
6. They... got annoyed
   ![Kevdog Round 4](https://genpkm.com/images/kevdog_4.jpg)
   ![Kevdog Purges Chat](https://genpkm.com/images/kevdog_purge.jpg)
7. They cropped my website out
8. I added more text around the code
   ![Kevdog Round 5](https://genpkm.com/images/kevdog_5.jpg)
9. They went back to OCR
10. I made the text OCR-unfriendly
    ![Kevdog Round 6](https://genpkm.com/images/kevdog_6.jpg)
11. They tried using contrast to hide my watermark
    ![Kevdog Round 7](https://genpkm.com/images/kevdog_7.jpg)
12. I marked up the whole image
    ![Kevdog Round 8](https://genpkm.com/images/kevdog_8.jpg)

Honestly, at this point I felt like I was in a bad rom-com. "Will they ever stop pursuing my raids? Find out after this commercial break!"

## 💡 The Final Boss Battle

Eventually, I played my trump card: an animated GIF with the raid code that was easy for humans to read but a nightmare for OCR:

![Raid Card GIF](https://genpkm.com/images/raid_card_2.gif)

This finally stopped them:

![Kevdog's Final Defeat](https://genpkm.com/images/kevdog_9.jpg)

So how were these digital pickpockets pulling off their heists? They were using "self-bots" that tap into Discord's API with authentication tokens. As long as they had an account lurking in my server, they could scrape data without interacting with anything. Sneaky, but not sneaky enough.

## 🎮 My Turn to Play

After defeating Team Rocket, I decided to have some fun of my own. I started posting Jessie and James' (Kai and Reedy's) public raids on my platforms. Six days later, they were still scratching their heads trying to figure out how to stop me.

Their brilliant countermeasure? A Discord bot posting fake raids for my scraper to pick up. Adorable attempt, really.

![Kai's Raids 1](https://genpkm.com/images/kai_raids1.jpg) | ![Kai's Raids 2](https://genpkm.com/images/kai_raids2.jpg)

Eventually, I stopped posting their raids. I just wanted to see if Jessie could come up with a solution, but I gave her too much credit. They truly are the joke of Team Rocket.

## 🛡️ The Final Solution

For all public raid bots, I've implemented a reaction system where users must react with an emoji on the Raid Embed to have the code DMed to them. This stops both plain-text scraping and OCR methods.

Unfortunately, this is necessary due to the rampant theft. This will protect hosts who only want their raids available to their intended users.

---

*And so, another day ends with Team Rocket blasting off again. Will they return with another harebrained scheme? Probably. Will it work? History suggests... no. Stay tuned for the next exciting episode!*
