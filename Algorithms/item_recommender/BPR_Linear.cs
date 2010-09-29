// Copyright (C) 2010 Zeno Gantner
//
// This file is part of MyMediaLite.
//
// MyMediaLite is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// MyMediaLite is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with MyMediaLite.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MyMediaLite.data;
using MyMediaLite.data_type;
using MyMediaLite.util;


namespace MyMediaLite.item_recommender
{
	/// <summary>
	/// Linear model optimized for BPR.
	/// No academic publications about this yet.
	///
	/// This engine does not support online updates.
	/// </summary>
	/// <author>Zeno Gantner, University of Hildesheim</a>
	public class BPR_Linear : Memory, ItemAttributeAwareRecommender, IterativeModel
	{
		protected BinaryAttributes item_attributes;
	    public int NumItemAttributes { get;	set; }

	    /// <summary>Item attribute weights</summary>
        protected Matrix<double> item_attribute_weight_by_user;

        /// <summary>Regularization parameter</summary>
        public double reg = 0.015;
        public double init_f_mean = 0;
        /// <summary>Standard deviation of the normal distribution used to initialize the features</summary>
        public double init_f_stdev = 0.1;
        /// <summary>Number of iterations over the training data</summary>
        public int num_iter = 30;

		/// <summary>Learning rate alpha</summary>
		public double learn_rate = 0.05;
		/// <summary>One iteration is <see cref="iteration_length"/> * number of entries in the training matrix</summary>
		protected int iteration_length = 5;

		protected System.Random random;

		/// <summary>Fast, but memory-intensive sampling</summary>
		bool fast_sampling = false;
		/// <summary>Fast sampling memory limit, in MiB</summary>
		public int fast_sampling_memory_limit = 1024;
		/// <summary>support data structure for fast sampling</summary>
		int[][] user_pos_items;
		/// <summary>support data structure for fast sampling</summary>
		int[][] user_neg_items;

		public override void Train()
		{
			random = util.Random.GetInstance();

			// prepare fast sampling, if necessary
			int support_data_size = ((max_user_id + 1) * (max_item_id + 1) * 4) / (1024 * 1024);
			Console.Error.WriteLine("BPR-LIN sds=" + support_data_size);
			if (support_data_size <= fast_sampling_memory_limit)
			{
				fast_sampling = true;

				user_pos_items = new int[max_user_id + 1][];
				user_neg_items = new int[max_user_id + 1][];
				for (int u = 0; u < max_user_id + 1; u++)
				{
					List<int> pos_list = new List<int>(data_user.GetRow(u));
					user_pos_items[u] = pos_list.ToArray();
					List<int> neg_list = new List<int>();
					for (int i = 0; i < max_item_id; i++)
						if (!data_user.GetRow(u).Contains(i) && data_item.GetRow(i).Count != 0)
							neg_list.Add(i);
					user_neg_items[u] = neg_list.ToArray();
				}
			}

        	item_attribute_weight_by_user = new Matrix<double>(max_user_id + 1, NumItemAttributes);
        	MatrixUtils.InitNormal(item_attribute_weight_by_user, init_f_mean, init_f_stdev);

			for (int i = 0; i < num_iter; i++)
			{
				Iterate();
				Console.Error.WriteLine(i);
			}
		}

		/// <summary>
		/// Perform one iteration of stochastic gradient ascent over the training data.
		/// One iteration is <see cref="iteration_length"/> * number of entries in the training matrix
		/// </summary>
		public void Iterate()
		{
			int num_pos_events = data_user.GetNumberOfEntries();

			for (int i = 0; i < num_pos_events * iteration_length; i++)
			{
				if (i % 1000000 == 999999)
					Console.Error.Write(".");
				if (i % 100000000 == 99999999)
					Console.Error.WriteLine();

				int user_id, item_id_1, item_id_2;
				SampleTriple(out user_id, out item_id_1, out item_id_2);

				UpdateFeatures(user_id, item_id_1, item_id_2);
			}
		}

		/// <summary>Sample a pair of items, given a user</summary>
		/// <param name="u">the user ID</param>
		/// <param name="i">the ID of the first item</param>
		/// <param name="j">the ID of the second item</param>
		protected  void SampleItemPair(int u, out int i, out int j)
		{
			if (fast_sampling)
			{
				int rindex;

				rindex = random.Next (0, user_pos_items[u].Length);
				i = user_pos_items[u][rindex];

				rindex = random.Next (0, user_neg_items[u].Length);
				j = user_neg_items[u][rindex];
			}
			else
			{
				HashSet<int> user_items = data_user.GetRow (u);
				i = user_items.ElementAt(random.Next (0, user_items.Count));
				do
					j = random.Next (0, max_item_id + 1);
				while (user_items.Contains(j) || data_item.GetRow(j).Count == 0); // don't sample the item if it never has been viewed (maybe unknown item!)
			}
		}

		/// <summary>Sample a user that has viewed at least one and not all items</summary>
		/// <returns>the user ID</returns>
		protected int SampleUser()
		{
			while (true)
			{
				int u = random.Next(0, max_user_id + 1);
				HashSet<int> user_items = data_user.GetRow(u);
				if (user_items.Count == 0 || user_items.Count == max_item_id + 1)
					continue;
				return u;
			}
		}

		/// <summary>Sample a triple for BPR learning</summary>
		/// <param name="u">the user ID</param>
		/// <param name="i">the ID of the first item</param>
		/// <param name="j">the ID of the second item</param>
		protected void SampleTriple(out int u, out int i, out int j)
		{
			u = SampleUser();
			SampleItemPair(u, out i, out j);
		}

		/// <summary>
		/// Modified feature update method that exploits attribute sparsity
		/// </summary>
		protected virtual void UpdateFeatures(int u, int i, int j)
		{
			double x_uij = Predict(u, i) - Predict(u, j);

			HashSet<int> attr_i = item_attributes.GetAttributes(i);
			HashSet<int> attr_j = item_attributes.GetAttributes(j);

			// assumption: attributes are sparse
			HashSet<int> attr_i_over_j = new HashSet<int>(attr_i);
			attr_i_over_j.ExceptWith(attr_j);
			HashSet<int> attr_j_over_i = new HashSet<int>(attr_j);
			attr_j_over_i.ExceptWith(attr_i);

			foreach (int a in attr_i_over_j)
			{
				double w_uf = item_attribute_weight_by_user.Get(u, a);
				double uf_update = 1 / (1 + Math.Exp(x_uij)) - reg * w_uf;
				item_attribute_weight_by_user.Set(u, a, w_uf + learn_rate * uf_update);
			}
			foreach (int a in attr_j_over_i)
			{
				double w_uf = item_attribute_weight_by_user.Get(u, a);
				double uf_update = -1 / (1 + Math.Exp(x_uij)) - reg * w_uf;
				item_attribute_weight_by_user.Set(u, a, w_uf + learn_rate * uf_update);
			}
		}

		/// <inheritdoc/>
        public override double Predict(int user_id, int item_id)
        {
            if ((user_id < 0) || (user_id >= item_attribute_weight_by_user.dim1))
            {
                Console.Error.WriteLine("user is unknown: " + user_id);
				return 0;
            }
            if ((item_id < 0) || (item_id > max_item_id))
            {
                Console.Error.WriteLine("item is unknown: " + item_id);
				return 0;
            }

			double result = 0;
			HashSet<int> attributes = this.item_attributes.GetAttributes(item_id);
			foreach (int a in attributes)
				result += item_attribute_weight_by_user.Get(user_id, a);
            return result;
        }

		public void SetItemAttributeData(SparseBooleanMatrix matrix, int num_attr)
		{
			this.item_attributes = new BinaryAttributes(matrix);
			this.NumItemAttributes = num_attr;

			// TODO check whether there is a match between num. of items here and in the collaborative data
		}

		/// <inheritdoc />
		public override void SaveModel(string filePath)
		{
			NumberFormatInfo ni = new NumberFormatInfo();
			ni.NumberDecimalDigits = '.';

			using ( StreamWriter writer = EngineStorage.GetWriter(filePath, this.GetType()) )
			{
				writer.WriteLine(item_attribute_weight_by_user.dim1 + " " + item_attribute_weight_by_user.dim2);
				for (int i = 0; i < item_attribute_weight_by_user.dim1; i++)
					for (int j = 0; j < item_attribute_weight_by_user.dim2; j++)
						writer.WriteLine(i + " " + j + " " + item_attribute_weight_by_user.Get(i, j).ToString(ni));
			}
		}

		/// <inheritdoc />
		public override void LoadModel(string filePath)
		{
			NumberFormatInfo ni = new NumberFormatInfo();
			ni.NumberDecimalDigits = '.';

            using ( StreamReader reader = EngineStorage.GetReader(filePath, this.GetType()) )
			{
            	string[] numbers = reader.ReadLine().Split(' ');
				int num_users = Int32.Parse(numbers[0]);
				int dim2 = Int32.Parse(numbers[1]);

				max_user_id = num_users - 1;
				Matrix<double> matrix = new Matrix<double>(num_users, dim2);
				int num_item_attributes = dim2;

            	while ((numbers = reader.ReadLine().Split(' ')).Length == 3)
            	{
					int i = Int32.Parse(numbers[0]);
					int j = Int32.Parse(numbers[1]);
					double v = Double.Parse(numbers[2], ni);

                	if (i >= num_users)
						throw new Exception(string.Format("Invalid user ID {0} is greater than {1}.", i, num_users - 1));
					if (j >= num_item_attributes)
						throw new Exception(string.Format("Invalid weight ID {0} is greater than {1}.", j, num_item_attributes - 1));

                	matrix.Set(i, j, v);
				}

				this.item_attribute_weight_by_user = matrix;
			}
		}

		public double ComputeFit()
		{
			// TODO
			return -1;
		}
		
		/// <inheritdoc/>
		public override string ToString()
		{
			return String.Format("BPR-Linear reg={0} num_iter={1} learn_rate={2} fast_sampling_memory_limit={3} init_f_mean={4} init_f_stdev={5}",
								  reg, num_iter, learn_rate, fast_sampling_memory_limit, init_f_mean, init_f_stdev);
		}

	}
}

