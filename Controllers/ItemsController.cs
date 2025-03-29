using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Coflnet.Sky.PlayerState.Models;
using System;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Services;
using MongoDB.Bson;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Controllers
{
    [ApiController]
    [Route("api/items")]
    public class ItemsController : ControllerBase
    {
        private readonly IItemsService _booksService;

        public ItemsController(IItemsService booksService) =>
            _booksService = booksService;

        [HttpPost]
        [Route("mock")]
        public async Task<IActionResult> Create()
        {
            var sourceData = "{\"rarity_upgrades\":1,\"gems\":{\"unlocked_slots\":[\"AMBER_0\",\"AMBER_1\",\"JADE_0\",\"JADE_1\",\"TOPAZ_0\"]},\"uid\":\"d8196ed3fcfa\"}";
            var data = JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(sourceData);
            Item newItem = new Item()
            {
                Tag = "ASPECT_OF_THE_END",
                Enchantments = new Dictionary<string, byte>() { { "sharpness", 1 } },
                ExtraAttributes = data// BsonDocument.Parse(sourceData) //new() { { "exp", 5 }, { "attr", new List<string>() { "kk", "bb" }.ToArray() } }
            };
            
            Console.WriteLine(data.ToBsonDocument().ToJson());
            Console.WriteLine(JsonConvert.SerializeObject(newItem.ExtraAttributes));
            var items = await _booksService.FindOrCreate(new Item[] { newItem });

            return CreatedAtAction(nameof(Get), new
            {
                id = items[0].Id
            });
        }

        [HttpPost]
        [Route("find/uuid")]
        public async Task<List<Item>> Get(List<ItemIdSearch> toSearch) =>
            await _booksService.FindItems(toSearch);

        [HttpGet]
        public async Task<List<Item>> Get([FromQuery] Item item) =>
            await _booksService.GetAsync(new Item[]{item});


        [HttpGet("{id}")]
        public async Task<ActionResult<Item>> Get(long id)
        {
            var book = await _booksService.GetAsync(id);

            if (book is null)
            {
                return NotFound();
            }

            return book;
        }

        [HttpPost]
        public async Task<IActionResult> Post(Item newItem)
        {
            await _booksService.CreateAsync(newItem);

            return CreatedAtAction(nameof(Get), new { id = newItem.Id }, newItem);
        }

        [HttpPut("{id:length(24)}")]
        public async Task<IActionResult> Update(long id, Item updatedItem)
        {
            var book = await _booksService.GetAsync(id);

            if (book is null)
            {
                return NotFound();
            }

            updatedItem.Id = book.Id;

            await _booksService.UpdateAsync(id, updatedItem);

            return NoContent();
        }

        [HttpDelete("{id:length(24)}")]
        public async Task<IActionResult> Delete(long id)
        {
            var book = await _booksService.GetAsync(id);

            if (book is null)
            {
                return NotFound();
            }

            await _booksService.RemoveAsync(id);

            return NoContent();
        }

        [HttpGet("recipe/{tag}")]
        public async Task<IEnumerable<Recipe>> GetRecipes(string tag, [FromServices] RecipeService recipeService)
        {
            return await recipeService.GetRecipes(tag);
        }
    }
}
